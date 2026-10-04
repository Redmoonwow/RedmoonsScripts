using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using ECommons.Hooks.ActionEffectTypes;
using ECommons.ImGuiMethods;
using ECommons.Logging;
using ECommons.SimpleGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using Splatoon.Memory;
using Splatoon.SplatoonScripting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RedmoonsScripts.Duties.Dawntrail.Dancing_Mad;

/// <summary>
/// Dancing Mad (Ultimate) P4 呪詛の叫声 (視線) を、Daily Routines の向き固定で自動で受ける。
/// </summary>
/// <remarks>
/// 呪詛の叫声 (5543) を持つ人は、デバフが切れた瞬間に視線を撃つ。本物なら見てはいけない、
/// 嘘なら見なければいけない。本物/嘘の見分けは P4_Debuff_Reminder と同じ仕組みを抜き出したもの:
///   1. OnVFXSpawn           分身が湧いたエフェクトで、その分身が正直か嘘つきかを覚える
///   2. OnActionEffectEvent  技を撃った分身から「今の回が嘘か」を確定する
///   3. OnGainBuffEffect     嘘の回に付いた呪詛の叫声を 1 個ずつ記録する
///
/// 同じ時刻に切れる保持者の組を「波」と呼ぶ。短い組と長い組があり、切れる時刻が約 9 秒離れている。
/// 波が来たら、その波の保持者を視線の発生源にして向きを決める:
///   ・自分が保持者 → 自分の視線は自分に当たらないので、もう 1 人だけを見る/見ない
///   ・自分が非保持者 → 2 人を同時に見る/見ない。2 人への方向の二等分線を使う
/// 二等分線は「2 人の位置の中点」ではない。中点を向くと、2 人の距離が違うとき近い方が
/// 視線の扇から外れる (北 3m / 東 15m の 2 人を見るとき、中点だと近い方が -78.7° で失敗し、
/// 二等分線なら ±45° に収まる)。方向を単位ベクトルにしてから足すのはそのため。
///
/// 向きの固定は Daily Routines の AutoFaceCameraDirection に頼む。このモジュールは読み込まれて
/// いるあいだ常にキャラをカメラ方向へ向け続けるので、普段は切っておき、波のときだけ読み込んで
/// 終わったら戻す。モジュールは詠唱中に向きを送らないので、発動の瞬間に詠唱していると効かない。
///
/// 呪詛の叫声が付いている間は、真上から見たレーダーを枠なしの別ウィンドウで出す (全員のデバフが消えたら消える)。
/// 波をつかむ前から「次の波」を仮に組んで、計画の扇まで描く。普段はクリックを素通りさせる。
/// 位置・大きさ・不透明度は Debug の「レーダー」で。デザインモードの間だけダミーを出し、ドラッグで動かせる。
///
/// 設定画面の Debug 欄は 5 タブ:
///   現在   今の波、真上から見たレーダー、発動の瞬間に取った判定 (次の発動まで残る)、
///          発動 ±2 秒の計画/送信/実際を 100ms ごとに取ったスナップショット (履歴にも残る)
///   レーダー 別ウィンドウのレーダーの表示/大きさ/不透明度、デザインモード (ダミー表示で位置合わせ)
///   記録   分身の正直/嘘つき、嘘の回に付いたデバフ、今の保持者と「今つかんだらこう判定する」
///   テスト 好きな人を発生源にした波を本物と同じ道で流す / Daily Routines を手で叩いて向きを確かめる
///   履歴   判断ログ。VerboseLog が切れていても残す
///
/// 下の Api は Generic/DailyRoutinesIPC.cs の Api をそのまま貼ったもの。直すときは向こうを
/// 直してから貼り直す (Splatoon はスクリプトを 1 ファイルずつ別にコンパイルするため共有できない)。
/// </remarks>
internal unsafe class P4_LockFaces : SplatoonScript<P4_LockFaces.Config>
{
    #region types
    /********************************************************************/
    /* types                                                            */
    /********************************************************************/
    private enum State
    {
        None = 0,   // 波を待っている
        Loading,    // 波をつかんだ。モジュールが立ち上がるのを待っている
        Locked,     // 向きを固定している。dry run なら計算して見せるだけ
    }

    /// <summary>波に含まれる視線の発生源 1 人。</summary>
    private sealed class WaveSource
    {
        public uint EntityId;
        public string Name = "";
        public bool Fake;                // 嘘 = 見なければいけない
        public float RemainingAtLatch;   // 波をつかんだ時点の残り秒数

        // ここから下は Debug 表示用。毎フレーム書き直す
        public bool Present;             // 今フレームに位置が取れたか
        public bool IsSelf;              // 自分自身か (自分の視線は自分に当たらない)
        public Vector2 Unit;             // 自分からこの人への単位ベクトル (X, Z)
        public float PlannedAngle;       // 計画した向きから見たこの人の角度 (度、0〜180)
        public float ActualAngle;        // 実際の向きから見たこの人の角度
    }

    /// <summary>発動の瞬間に取った判定 1 行。Ok = null は判定しようがない (自分 / 見えない)。</summary>
    private readonly record struct FireLine(string Text, bool? Ok);

    /// <summary>発動前後のスナップショットの 1 行。Tick は TickCount64 (発動からの差は書き出すときに出す)。</summary>
    /// <summary>レーダーの点 1 つ。Offset は自分からの (X, Z) [m]。Later = 次の波より後の組 (灰色)。</summary>
    private readonly record struct RadarPoint(Vector2 Offset, bool Fake, bool Later, bool IsSelf, string Label);

    private readonly record struct Sample(long Tick, State State, float? Planned, float? Sent, float Actual, bool Casting, bool Fire);

    #endregion

    #region class
    /********************************************************************/
    /* class                                                            */
    /********************************************************************/
    public class Config
    {
        public float LeadSeconds = 2.0f;   // 視線の何秒前から向きを固定するか
        public float HoldSeconds = 1.0f;   // 視線の何秒後まで固定を続けるか
        public bool UnloadAfter = true;    // 自分で読み込んだモジュールを、終わったら戻すか
        public bool VerboseLog;            // 波をつかむ/固定する/外すたびにログを残す

        // ---- レーダーのウィンドウ (Debug の「レーダー」で変える) ----
        public bool ShowRadar = true;                          // デバフが付いている間レーダーを出すか
        public Vector2 RadarPosition = DefaultRadarPosition;   // 画面上の左上の位置
        public float RadarSize = 220f;                         // 一辺 (px)
        public float RadarOpacity = 0.8f;                      // 全体の不透明度 (0.1〜1)
    }

    /// <summary>レーダーだけを出す枠なしのウィンドウ。</summary>
    /// <remarks>ECommons の EzConfigGui が持つ Dalamud の WindowSystem に載せる (公式の P3 Dive from Grace Neo と同じやり方)。
    /// OnEnable で作って OnDisable で外す。外し忘れると、スクリプトを無効にしても枠が残る。
    /// Dancing Mad の外でもデザインモードを入れたときだけは作る (EnsureRadarWindow)。
    /// 出すかどうかは毎フレーム DrawConditions で決める (IsOpen は開けっぱなし)。
    /// 普段は NoInputs でクリックを素通りさせ、デザインモードの間だけドラッグで動かせる。
    /// ドラッグで動いた位置は C.RadarPosition に書き戻す。設定ファイルへの保存は、Splatoon が
    /// スクリプトを無効にするとき (エリア移動など) と設定画面を閉じるときに行われる。</remarks>
    private sealed class RadarWindow : Window, IDisposable
    {
        private const ImGuiWindowFlags BaseFlags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse;

        private readonly P4_LockFaces _script;

        public RadarWindow(P4_LockFaces script) : base("P4 LockFaces Radar###P4LFRadar", BaseFlags, true)
        {
            _script = script;
            IsOpen = true;
            ShowCloseButton = false;
            RespectCloseHotkey = false;
            AllowPinning = false;
            AllowClickthrough = false;
            DisableWindowSounds = true;
            DisableFadeInFadeOut = true;
            BgAlpha = 0f;   // 背景はレーダー自身が描く (不透明度を効かせるため)
            EzConfigGui.WindowSystem.AddWindow(this);
        }

        public void Dispose() => EzConfigGui.WindowSystem.RemoveWindow(this);

        public override bool DrawConditions() => _script.RadarVisible;

        /// <remarks>位置は初回だけ設定値から置く (FirstUseEver)。以降は ImGui が覚えている位置を使い、
        /// 「位置を初期値に戻す」が押されたフレームだけ Always で上書きする。</remarks>
        public override void PreDraw()
        {
            var c = _script.C;
            Flags = BaseFlags | (_script.DesignActive ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoInputs);
            Position = c.RadarPosition;
            PositionCondition = _script._radarForcePosition ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
            _script._radarForcePosition = false;
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        }

        public override void PostDraw() => ImGui.PopStyleVar(2);

        public override void Draw()
        {
            var c = _script.C;
            _script.DrawRadarCanvas(c.RadarSize, c.RadarOpacity, _script.DesignActive);
            c.RadarPosition = ImGui.GetWindowPos();
        }
    }

    /// <summary>Daily Routines の IPC をまとめたもの。これだけコピーすれば動く。</summary>
    /// <remarks>
    /// 貼り先に必要な using:
    ///   using Dalamud.Game.ClientState.Conditions;  using ECommons.DalamudServices;
    ///   using ECommons.EzIpcManager;  using System;  using System.Linq;  using System.Numerics;
    ///
    /// 使い方:
    ///   private readonly Api _dr = new();
    ///   public override void OnSetup() => _dr.Init();
    ///   if (_dr.AutoRepairIsNeedToRepair == true) _dr.EnqueueRepair();
    ///
    /// 設計上の約束:
    ///   ・<c>[EzIPC]</c> は **フィールドにしか付けない**。メソッドに付けると EzIPC がそれを
    ///     「提供側」として登録してしまい、こちらが IPC を生やす側になってしまう。
    ///   ・全呼び出しを <c>SafeWrapper.AnyException</c> で包む。プラグイン不在・モジュール無効・
    ///     型の食い違いのいずれでも例外を投げず既定値を返す。握り潰した中身を見たいときは
    ///     <c>EzIPC.OnSafeInvocationException</c> を購読する。
    ///   ・モジュールごとの IPC はそのモジュールが有効なときしか登録されない。戻り値の false が
    ///     「本当に false」か「呼べていない」か区別できないので、読み取りは <see cref="Gate"/> を
    ///     通して <c>bool?</c> で返す。null = 呼べていない。
    ///   ・キャラや設定を動かすものは <see cref="Mutate"/> を通す。duty recorder 再生中は
    ///     送らない。録画を回して検証している最中に実機を動かさないため。
    ///
    /// 名前は属性の文字列がそのまま IPC 名になる仕様 (OmenTools の <c>IPCAttributeRegistry</c>) で、
    /// prefix は <see cref="Init"/> 1 か所にまとめてある。戻り値の無いものは提供側が TRet に
    /// <c>typeof(object)</c> を使っており、EzIPC の既定 (<c>ActionLastGenericType</c>) と一致するので
    /// Action フィールドで素直に通る。
    /// </remarks>
    public sealed class Api
    {
        private const string PluginName = "DailyRoutines";
        private bool _initialized;

        // ---- 本体 -------------------------------------------------------------
        [EzIPC("IsModuleEnabled")] private Func<string, bool?> _isModuleEnabled = null!;
        [EzIPC("Version")] private Func<Version> _version = null!;
        [EzIPC("LoadModule")] private Func<string, bool, bool> _loadModule = null!;
        [EzIPC("UnloadModule")] private Func<string, bool, bool, bool> _unloadModule = null!;

        // ---- AutoRepair -------------------------------------------------------
        [EzIPC("Modules.AutoRepair.IsBusy")] private Func<bool> _repairIsBusy = null!;
        [EzIPC("Modules.AutoRepair.IsNeedToRepair")] private Func<bool> _repairIsNeeded = null!;
        [EzIPC("Modules.AutoRepair.IsAbleToRepair")] private Func<bool> _repairIsAble = null!;
        [EzIPC("Modules.AutoRepair.EnqueueRepair")] private Action _repairEnqueue = null!;

        // ---- AutoAetherialReduction / FastGrandCompanyExchange / AutoDiscard ----
        [EzIPC("Modules.AutoAetherialReduction.IsBusy")] private Func<bool> _reductionIsBusy = null!;
        [EzIPC("Modules.AutoAetherialReduction.StartReduction")] private Func<bool> _reductionStart = null!;
        [EzIPC("Modules.FastGrandCompanyExchange.IsBusy")] private Func<bool> _gcExchangeIsBusy = null!;
        [EzIPC("Modules.AutoDiscard.IsBusy")] private Func<bool> _discardIsBusy = null!;

        // ---- AutoSpeedMultiplier ----------------------------------------------
        [EzIPC("Modules.AutoSpeedMultiplier.ChangeMultiplier")] private Action<float> _speedChange = null!;
        [EzIPC("Modules.AutoSpeedMultiplier.GetMultiplier")] private Func<float> _speedGet = null!;

        // ---- AutoFaceCameraDirection ------------------------------------------
        [EzIPC("Modules.AutoFaceCameraDirection.SetWorkMode")] private Action<bool> _faceSetWorkMode = null!;
        [EzIPC("Modules.AutoFaceCameraDirection.CancelLockOn")] private Action _faceCancel = null!;
        [EzIPC("Modules.AutoFaceCameraDirection.LockOnGround")] private Func<string, bool> _faceLockGround = null!;
        [EzIPC("Modules.AutoFaceCameraDirection.LockOnChara")] private Action<float> _faceLockChara = null!;
        [EzIPC("Modules.AutoFaceCameraDirection.LockOnCamera")] private Action<float> _faceLockCamera = null!;

        // ---- AutoAntiKnockback ------------------------------------------------
        [EzIPC("Modules.AutoAntiKnockback.ReplayKnockback")] private Action _knockbackReplay = null!;
        [EzIPC("Modules.AutoAntiKnockback.ChangeMethod")] private Func<int, bool> _knockbackMethod = null!;
        [EzIPC("Modules.AutoAntiKnockback.AdjustDistanceMultiplier")] private Action<float> _knockbackDistance = null!;

        // ---- OptimizedFriendlist / AutoUseEventItem ---------------------------
        [EzIPC("Modules.OptimizedFriendlist.GetRemarkByContentID")] private Func<ulong, string> _friendRemark = null!;
        [EzIPC("Modules.OptimizedFriendlist.GetNicknameByContentID")] private Func<ulong, string> _friendNickname = null!;
        [EzIPC("Modules.AutoUseEventItem.UseEventItem")] private Action _useEventItem = null!;

        /// <summary>Daily Routines が読み込まれているか。</summary>
        public bool Available =>
            Svc.PluginInterface.InstalledPlugins.Any(x => x.IsLoaded && x.InternalName == PluginName);

        public Version? PluginVersion => Available ? _version() : null;

        // ---- 読み取り: null = 呼べていない ------------------------------------
        public bool? AutoRepairIsBusy => Gate("AutoRepair") ? _repairIsBusy() : null;
        public bool? AutoRepairIsNeedToRepair => Gate("AutoRepair") ? _repairIsNeeded() : null;
        public bool? AutoRepairIsAbleToRepair => Gate("AutoRepair") ? _repairIsAble() : null;
        public bool? AetherialReductionIsBusy => Gate("AutoAetherialReduction") ? _reductionIsBusy() : null;
        public bool? GrandCompanyExchangeIsBusy => Gate("FastGrandCompanyExchange") ? _gcExchangeIsBusy() : null;
        public bool? AutoDiscardIsBusy => Gate("AutoDiscard") ? _discardIsBusy() : null;
        public float? SpeedMultiplier => Gate("AutoSpeedMultiplier") ? _speedGet() : null;

        /// <summary>購読を張る。OnSetup から 1 回呼ぶ。</summary>
        /// <remarks>OnEnable では遅い。ValidTerritories の外ではスクリプトが有効にならず、そこで設定画面を開くと
        /// 未初期化のフィールド (null) を呼んで落ちる。
        /// 2 回呼んでも購読が重複するだけで害は無いが、無駄なので閂を掛けてある。
        /// Daily Routines が入っていなくても失敗しない。購読は張れて、呼んだときに既定値が返る。</remarks>
        public void Init()
        {
            if (_initialized) return;
            _initialized = true;
            EzIPC.Init(this, PluginName, SafeWrapper.AnyException);
        }

        /// <summary>モジュールの状態。true=有効 / false=無効 / null=そんな名前は無い。</summary>
        /// <remarks>名前は大文字小文字を区別しない。</remarks>
        public bool? IsModuleEnabled(string module) => Available ? _isModuleEnabled(module) : null;

        public bool LoadModule(string module, bool affectsConfig = false) =>
            Mutate() && _loadModule(module, affectsConfig);

        public bool UnloadModule(string module, bool affectsConfig = false, bool force = false) =>
            Mutate() && _unloadModule(module, affectsConfig, force);

        public string? FriendRemark(ulong contentId) =>
            Gate("OptimizedFriendlist") ? _friendRemark(contentId) : null;

        public string? FriendNickname(ulong contentId) =>
            Gate("OptimizedFriendlist") ? _friendNickname(contentId) : null;

        // ---- 実行: 戻り値は「送れたか」。null 返しは相手の戻り値が要るものだけ ----
        public bool EnqueueRepair() => Send("AutoRepair", _repairEnqueue);
        public bool ReplayKnockback() => Send("AutoAntiKnockback", _knockbackReplay);
        public bool UseEventItem() => Send("AutoUseEventItem", _useEventItem);
        public bool CancelFacingLock() => Send("AutoFaceCameraDirection", _faceCancel);

        public bool? StartAetherialReduction() =>
            Gate("AutoAetherialReduction") && Mutate() ? _reductionStart() : null;

        /// <summary>移動速度の倍率を変える。0〜10。</summary>
        public bool ChangeSpeedMultiplier(float multiplier) =>
            Send("AutoSpeedMultiplier", () => _speedChange(Math.Clamp(multiplier, 0.0f, 10.0f)));

        // ---- 向きの固定 ---------------------------------------------------------
        // どれも「固定する」だけで、CancelFacingLock を呼ぶまで外れない。一発で振り向く API ではない。
        // 固定中はモジュールが毎フレーム向きを書き戻し、33ms (duty 内) / 100ms 間隔で
        // 位置パケットを送り続ける。詠唱中は送らない。
        //
        // 3 つの IPC は角度の規約がそれぞれ違う。モジュールの中では全部 LockOnChara の値
        // (= キャラの Rotation) に変換されて同じ変数に入る:
        //   LockOnGround(string)  "north" など 8 方位。**小文字限定**。IPC 経路は小文字化しない
        //   LockOnChara(float)    キャラの Rotation そのもの。ラジアン、0=南 / +π/2=東 / ±π=北 / -π/2=西
        //   LockOnCamera(float)   カメラの DirH。キャラの向き = DirH + π
        // 自分のコードの角度 (コンパス方位・DirectionCalculator) から入れるなら
        // LockFacingOnBearing / LockFacingToward を使う。変換をここで 1 回だけ書くため。

        /// <summary>SetWorkMode。false が普通の状態。</summary>
        /// <remarks>true にすると意味が反転し、DailyRoutines の「打断キー」を押している間だけ
        /// 固定が効くようになる。スクリプトから固定したいなら false のまま触らない。</remarks>
        public bool SetFacingWorkMode(bool enabled) =>
            Send("AutoFaceCameraDirection", () => _faceSetWorkMode(enabled));

        /// <summary>8 方位の名前で向きを固定する。"north" "northeast" … "northwest"。</summary>
        /// <remarks>受け側は <c>FrozenDictionary</c> の既定比較 (大文字小文字を区別) で引いており、
        /// /pdrface コマンドと違って IPC 経路は小文字化しない。"North" を渡すと黙って false が返る。
        /// ここで小文字にしてから渡す。</remarks>
        public bool? LockFacingOnGround(string direction) =>
            Gate("AutoFaceCameraDirection") && Mutate() ? _faceLockGround(direction.ToLowerInvariant()) : null;

        /// <summary>キャラの Rotation (ラジアン) で向きを固定する。</summary>
        /// <remarks>0=南 / +π/2=東 / ±π=北 / -π/2=西。反時計回りが正。
        /// <c>IGameObject.Rotation</c> と同じ値なので、他人の向きを写すならそのまま渡せる。</remarks>
        public bool LockFacingOnChara(float rotation) =>
            Send("AutoFaceCameraDirection", () => _faceLockChara(NormalizeChara(rotation)));

        /// <summary>カメラの DirH で向きを固定する。キャラはその反対 (DirH + π) を向く。</summary>
        /// <remarks>受け側の変換は結果を [0, 2π) で返すので、キャラの Rotation の範囲 [-π, π] と
        /// ずれる。スクリプトから使う理由はほぼ無い。LockFacingOnChara の方が素直。</remarks>
        public bool LockFacingOnCamera(float dirH) =>
            Send("AutoFaceCameraDirection", () => _faceLockCamera(dirH));

        /// <summary>コンパス方位 (度) で向きを固定する。0=北 / 90=東 / 180=南 / 270=西、時計回り。</summary>
        /// <remarks><c>MathHelper.GetRelativeAngle</c> と同じ規約。本人の <c>FaceTarget(rot)</c> の引数とも同じ。
        /// Rotation = π − 方位。Splatoon の <c>Utils.GetRotationWithOverride</c> と同じ式。
        /// DirectionCalculator の角度 (東=0 の時計回り) から来るなら、方位 = その角度 + 90。</remarks>
        public bool LockFacingOnBearing(float degrees) =>
            LockFacingOnChara(MathF.PI - degrees * MathF.PI / 180f);

        /// <summary><paramref name="from"/> から <paramref name="target"/> の方を向いて固定する。</summary>
        /// <remarks>受け側の <c>WorldDirHToChara</c> と同じ <c>atan2(ΔX, ΔZ)</c>。
        /// 方位を経由しないので度とラジアン、時計回りと反時計回りを一度も混ぜずに済む。
        /// 自分を回すなら from に BasePlayer.Position を渡す (Api からは BasePlayer が見えないため引数にしてある)。
        /// 2 点がほぼ重なっていると向きが決まらないので、そのときは何もしない。</remarks>
        public bool LockFacingToward(Vector3 from, Vector3 target)
        {
            var dx = target.X - from.X;
            var dz = target.Z - from.Z;
            if (dx * dx + dz * dz < 0.0001f) return false;
            return LockFacingOnChara(MathF.Atan2(dx, dz));
        }

        /// <summary>角度を (-π, π] に畳む。キャラの Rotation の範囲に合わせる。</summary>
        /// <remarks>方位からの変換は -π を少し下回る値を作りうる (方位 360 付近)。
        /// 受け側は値をそのまま書くので、ここで範囲に戻してから渡す。</remarks>
        private static float NormalizeChara(float rotation)
        {
            var r = MathF.IEEERemainder(rotation, MathF.Tau);
            return r <= -MathF.PI ? r + MathF.Tau : r;
        }

        /// <summary>ノックバック処理の方式を変える。0〜4。</summary>
        public bool? ChangeKnockbackMethod(int method) =>
            Gate("AutoAntiKnockback") && Mutate() ? _knockbackMethod(Math.Clamp(method, 0, 4)) : null;

        public bool AdjustKnockbackDistance(float multiplier) =>
            Send("AutoAntiKnockback", () => _knockbackDistance(multiplier));

        /// <summary>そのモジュールを今叩けるか。</summary>
        /// <remarks>プラグインが居て、かつモジュールが有効なときだけ true。無効なモジュールの IPC は
        /// 登録されておらず、呼んでも既定値が返るだけで「本当に false」と区別が付かないため、
        /// 呼ぶ前にここで弾く。</remarks>
        private bool Gate(string module) => Available && _isModuleEnabled(module) == true;

        /// <summary>ゲーム側を動かしてよい状況か。</summary>
        /// <remarks>duty recorder 再生中は false。録画を見ているだけのつもりで実機の速度や向きが
        /// 変わると事故になる。読み取りは再生中でも通す。</remarks>
        private static bool Mutate() => !Svc.Condition[ConditionFlag.DutyRecorderPlayback];

        /// <summary>戻り値の無い IPC を叩く。送れたかどうかだけ返す。</summary>
        private bool Send(string module, Action call)
        {
            if (!Gate(module) || !Mutate()) return false;
            call();
            return true;
        }
    }

    #endregion

    #region const
    /********************************************************************/
    /* const                                                            */
    /********************************************************************/
    // 呪詛の叫声。452 は旧 ID (P4_Debuff_Reminder の Debuff.LookAway と同じ並び)
    private static readonly uint[] LookAwayIds = [5543, 452];

    private const string FaceModule = "AutoFaceCameraDirection";
    private const float ConeHalfAngle = 45f;   // 視線の扇の半角 (度)。Satisfies の説明を参照
    private const int HistoryMax = 300;        // 履歴タブに残す行数
    private const long SnapshotWindowMs = 2000;    // 発動の前後何 ms をスナップショットに取るか
    private const long SnapshotIntervalMs = 100;   // スナップショットの間隔 (発動のフレームは間隔に関係なく取る)
    private const float GroupSeconds = 2f;         // 先頭からこの秒数以内に切れる人を同じ波にする
    private static readonly Vector2 DefaultRadarPosition = new(100f, 300f);

    // デザインモードのダミー。次の組 2 人 (嘘 = 見る) と後の組 2 人。実際の配置に近い距離にしてある
    private static readonly RadarPoint[] DummyPoints =
    [
        new(new Vector2(6f, -8f), true, false, false, "Dummy A"),
        new(new Vector2(-7f, -4f), true, false, false, "Dummy B"),
        new(new Vector2(5f, 9f), false, true, false, "9s"),
        new(new Vector2(-9f, 6f), false, true, false, "9s"),
    ];

    // テストタブの「〜を向く」。コンパス方位 (度、0=北の時計回り)
    private static readonly (string Label, float Bearing)[] Bearings = [("北", 0f), ("東", 90f), ("南", 180f), ("西", 270f)];

    #endregion

    #region public properties
    /********************************************************************/
    /* public properties                                                */
    /********************************************************************/
    public override HashSet<uint>? ValidTerritories { get; } = [1363];   // Dancing Mad (Ultimate)
    public override Metadata Metadata => new(5, "Redmoon");

    #endregion

    #region private properties
    /********************************************************************/
    /* private properties                                               */
    /********************************************************************/
    private readonly Api _dr = new();

    // ---- 本物/嘘の記録。OnReset で戻す ---------------------------------------
    private readonly Dictionary<uint, bool> _isTruth = [];                   // 分身の EntityId -> 正直か
    private readonly HashSet<(uint EntityId, uint StatusId)> _fakeStatuses = [];   // 嘘の回に付いたもの
    private bool _isLie;                                                     // 今の回を撃ったのが嘘つきか

    // ---- 波 1 つぶん。Finish で戻す ------------------------------------------
    private State _state = State.None;
    private readonly List<WaveSource> _wave = [];
    private long _waveEndMs;             // 視線が発動する時刻 (TickCount64)
    private bool _dryRun;                // 送らずに計算だけする (リプレイ中 / Daily Routines が無い)
    private bool _loadRequested;         // LoadModule を 1 度だけ撃つための閂
    private bool _loadedByUs;            // モジュールを自分で読み込んだか。終わったら戻すため
    private float? _sentRotation;        // 最後に送った向き。同じ値を毎フレーム送らないため
    private float? _plannedRotation;     // 今フレームに計算した向き (Debug 用)
    private string _note = "";           // 直近の判断 (Debug 用)
    private bool _fired;                 // 発動の瞬間の判定を取ったか

    // ---- 波をまたいで残す。OnReset で戻す ------------------------------------
    private long _lastWaveEndMs;         // 直前に処理した波。切れかけのデバフでつかみ直さないため

    // ---- Debug 画面。リセットしない (前のトライを後から見るため) -------------
    private readonly List<string> _history = [];         // Log の中身。VerboseLog に関係なく残す
    private readonly List<FireLine> _lastFire = [];      // 直前の発動の瞬間の判定
    private string _lastFireHeader = "";
    private readonly Dictionary<uint, int> _testRole = [];   // テスト波の発生源。0=対象外 / 1=本物 / 2=嘘
    private float _testFireSeconds = 3f;
    private bool _testDryRun = true;

    // ---- 発動前後のスナップショット。波とは別に回す (波を終えても発動 +2s まで取り続ける) ----
    private long _snapFireMs;            // 記録中の発動時刻 (TickCount64)。0 = 記録していない
    private long _lastSnapFireMs;        // 直前に書き出した発動時刻。同じ波で取り直さないため。OnReset で戻す
    private long _lastSampleMs;
    private bool _snapFireMarked;        // 発動のフレームを取ったか
    private readonly List<Sample> _samples = [];
    private string _snapHeader = "";     // 波の中身。BeginWave で埋める
    private string _lastSnapshot = "";   // 直前に書き出したもの (Debug 表示用)

    // ---- レーダー。UpdateRadar が毎フレーム作り直し、ウィンドウと Debug が読む ----
    private RadarWindow? _radarWindow;   // OnEnable で作り OnDisable で外す
    private readonly List<WaveSource> _preview = [];   // 波をつかむ前に「次の波」を仮に組んだもの
    private readonly List<(uint EntityId, float Remaining)> _radarLater = [];   // 次の波より後に切れる保持者
    private float? _radarPlanned;        // 計画の向き。波の最中は _plannedRotation、その前は _preview から計算
    private float? _radarCountdown;      // 次の発動まで (秒)
    private readonly List<RadarPoint> _radarPoints = [];   // CollectRadarPoints が毎フレーム使い回す
    private bool _radarDesign;           // デザインモード。ダミーを出してドラッグで動かせる
    private long _lastSettingsDrawMs;    // 設定画面を最後に描いた時刻。閉じたらデザインモードを消すため
    private bool _radarForcePosition;    // 次のフレームで設定の位置へ戻す

    // 毎フレームの走査で使い回す。フェーズ中ずっと走るので確保しない
    private readonly List<(uint EntityId, string Name, uint StatusId, float Remaining)> _holders = [];

    /// <summary>ケフカ本体が居て殴れる = このフェーズの最中。</summary>
    private bool PhaseActive => Svc.Objects.Any(x => x.BaseId == 18475 && x.IsTargetable);

    /// <summary>レーダーに点で出す発生源。波をつかんでいればその波、まだなら次の波の仮組み。</summary>
    private List<WaveSource> RadarSources => _state != State.None ? _wave : _preview;

    /// <summary>レーダーのウィンドウを出すか。呪詛の叫声が誰かに付いてから、全員のぶんが消えて波を終えるまで。</summary>
    private bool RadarVisible =>
        BasePlayer != null && (DesignActive || C.ShowRadar && IsEnabled && (_state != State.None || _holders.Count > 0));

    /// <summary>デザインモードが効いているか。設定画面を閉じると (描かれなくなると) 1 秒で切れる。</summary>
    /// <remarks>設定画面の描画で判定しているので、スクリプトを読み直して古いインスタンスが残っても、
    /// その古いウィンドウはここが false になって出なくなる。</remarks>
    private bool DesignActive => _radarDesign && Environment.TickCount64 - _lastSettingsDrawMs < 1000;

    #endregion

    #region public methods
    /********************************************************************/
    /* public methods                                                   */
    /********************************************************************/
    // 本物/嘘の記録は P4_Debuff_Reminder の OnVFXSpawn / OnActionEffectEvent / OnGainBuffEffect を
    // 呪詛の叫声だけに絞って写したもの。向きの判断は OnUpdate から private に出してある。

    public override void OnSetup() => _dr.Init();

    public override void OnEnable() => EnsureRadarWindow();

    public override void OnDisable()
    {
        Finish("disable");
        FlushSnapshot("disable");
        _radarWindow?.Dispose();
        _radarWindow = null;
        _holders.Clear();
    }

    public override void OnReset()
    {
        Finish("reset");
        FlushSnapshot("reset");
        _isTruth.Clear();
        _fakeStatuses.Clear();
        _lastWaveEndMs = 0;
        _lastSnapFireMs = 0;
    }

    public override void OnUpdate()
    {
        ScanHolders();
        if (_state == State.None)
        {
            if (BasePlayer != null && PhaseActive) TryLatchWave();
        }
        else
        {
            Advance();
        }
        SampleSnapshot();   // Advance の後。今フレームの計画/送信を取るため
        UpdateRadar();
    }

    public override void OnVFXSpawn(uint target, string vfxPath)
    {
        // 19510 / 19507 = 正直と嘘つきの分身。どちらがどちらかは湧いたときのエフェクトでしか分からない
        if (target.GetObject()?.BaseId.EqualsAny<uint>(19510, 19507) != true) return;

        if (vfxPath is "vfx/common/eff/z3oy_stlp7_c0c.avfx" or "vfx/common/eff/z3oy_stlp5_c0c.avfx")
            _isTruth[target] = true;
        else if (vfxPath is "vfx/common/eff/z3oy_stlp6_c0c.avfx" or "vfx/common/eff/z3oy_stlp4_c0c.avfx")
            _isTruth[target] = false;
    }

    public override void OnActionEffectEvent(ActionEffectSet set)
    {
        // デバフが付くのはこの直後なので、ここで先に今の回の真偽を確定しておく
        if (set.Action == null || set.Source is not { } source) return;
        if (_isTruth.TryGetValue(source.EntityId, out var truth)) _isLie = !truth;
    }

    public override void OnGainBuffEffect(uint sourceId, FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        // sourceId は名前に反して「デバフが付いた側」(P4_Debuff_Reminder と同じ扱い)
        if (!PhaseActive || !LookAwayIds.Contains(status.StatusId)) return;
        if (!sourceId.TryGetPlayer(out _)) return;
        if (!_isLie) return;

        _fakeStatuses.Add((sourceId, status.StatusId));
        Log($"嘘の呪詛の叫声を記録: 0x{sourceId:X8}");
    }

    public override void OnStartingCast(uint sourceId, PacketActorCast* packet)
    {
        // 49884 = このフェーズの開始技。前の周回の記録をここで捨てる
        if (packet->ActionType == (byte)ActionType.Action && packet->ActionID == 49884)
            Controller.Reset();
    }

    public override void OnSettingsDraw()
    {
        // 一度閉じてから開き直したなら、デザインモードは切っておく (開きっぱなしの事故を防ぐ)
        var now = Environment.TickCount64;
        if (now - _lastSettingsDrawMs > 1000) _radarDesign = false;
        _lastSettingsDrawMs = now;

        DrawSettings();
        if (!ImGuiEx.CollapsingHeader("Debug")) return;
        ImGuiEx.EzTabBar("##P4LFDebug",
            ("現在", DrawNowTab, null, false),
            ("レーダー", DrawRadarTab, null, false),
            ("記録", DrawRecordTab, null, false),
            ("テスト", DrawTestTab, null, false),
            ("履歴", DrawHistoryTab, null, false));
    }

    #endregion

    #region private methods
    /********************************************************************/
    /* private methods                                                  */
    /********************************************************************/

    /// <summary>呪詛の叫声を持っている人を _holders に集め直す。毎フレーム。</summary>
    private void ScanHolders()
    {
        _holders.Clear();
        foreach (var pc in Controller.GetPartyMembers())
            foreach (var s in pc.StatusList)
            {
                if (!LookAwayIds.Contains(s.StatusId) || s.RemainingTime <= 0f) continue;
                _holders.Add((pc.EntityId, pc.Name.ToString(), s.StatusId, s.RemainingTime));
                break;
            }
    }

    /// <summary>次の視線の波が近づいていたら、その波の保持者をつかむ。</summary>
    /// <remarks>一番早く切れる人の残りが LeadSeconds を切った瞬間に、その人から 2 秒以内に
    /// 切れる人を全員まとめて 1 つの波にする。短い組と長い組は約 9 秒離れているので混ざらない。
    /// 本物/嘘はつかんだ時点で確定させる。視線が撃たれるころにはデバフが消えていて引けないため。
    ///
    /// 直前に処理した波と同じ時刻に切れるものは拾わない。固定を解いた直後、消えかけのデバフが
    /// 残り 0 秒付近で数フレーム見えることがあるため。</remarks>
    private void TryLatchWave()
    {
        if (_holders.Count == 0) return;

        var first = _holders.Min(h => h.Remaining);
        var now = Environment.TickCount64;
        var endMs = now + (long)(first * 1000f);
        if (first * 1000f <= SnapshotWindowMs) BeginSnapshot(endMs, false);   // LeadSeconds が短くても 2 秒前から取る
        if (first > C.LeadSeconds) return;
        if (Math.Abs(endMs - _lastWaveEndMs) < 1500) return;

        _wave.Clear();
        foreach (var h in _holders.Where(h => h.Remaining <= first + GroupSeconds))
            _wave.Add(new WaveSource
            {
                EntityId = h.EntityId,
                Name = h.Name,
                Fake = _fakeStatuses.Contains((h.EntityId, h.StatusId)),
                RemainingAtLatch = h.Remaining,
            });
        BeginWave(endMs, false, "波をつかんだ");
    }

    /// <summary>_wave に詰めた発生源で波を始める。本物の波とテスト波の共通部分。</summary>
    /// <param name="forceDry">true なら Daily Routines が居ても送らない (テスト用)。</param>
    private void BeginWave(long endMs, bool forceDry, string origin)
    {
        _waveEndMs = endMs;
        _lastWaveEndMs = endMs;
        _loadRequested = false;
        _loadedByUs = false;
        _sentRotation = null;
        _fired = false;
        _dryRun = forceDry || Svc.Condition[ConditionFlag.DutyRecorderPlayback] || !_dr.Available;
        _state = _dryRun || _dr.IsModuleEnabled(FaceModule) == true ? State.Locked : State.Loading;
        _note = !_dryRun ? "" : forceDry ? "dry run (テスト)" : "dry run (リプレイ中か Daily Routines が無い)";
        var members = string.Join(", ", _wave.Select(w => $"{w.Name}={(w.Fake ? "嘘" : "本物")}"));
        Log($"{origin}: 発動まで {(endMs - Environment.TickCount64) / 1000f:F2}s / {members} / state={_state} dry={_dryRun}");

        BeginSnapshot(endMs, true);
        if (_snapFireMs != 0) _snapHeader = $"{origin} / {members} / dry={_dryRun}";
    }

    /// <summary>つかんだ波を 1 フレーム進める。モジュールの読み込み → 向きの送信 → 終了判定。</summary>
    private void Advance()
    {
        var now = Environment.TickCount64;
        if (now > _waveEndMs + (long)(C.HoldSeconds * 1000f))
        {
            if (!_fired) RecordFire();   // HoldSeconds=0 だと発動のフレームを飛び越えることがある
            Finish("発動から HoldSeconds 経過");
            return;
        }
        if (BasePlayer == null || BasePlayer.IsDead) { Finish("自分が居ない/死んでいる"); return; }

        if (_state == State.Loading) AdvanceLoading();

        // 読み込み待ちでも計算はする。Debug で「何を向くつもりか」を先に見られるように
        _plannedRotation = ComputeFacing(_wave);
        if (!_fired && now >= _waveEndMs) RecordFire();
        if (_state != State.Locked || _dryRun || _plannedRotation is not { } rotation) return;

        // 0.01 rad (約 0.6°) 未満の変化は送らない。IPC とモジュールの書き戻しを毎フレーム起こさないため
        if (_sentRotation is { } prev && MathF.Abs(MathF.IEEERemainder(rotation - prev, MathF.Tau)) < 0.01f) return;
        if (_dr.LockFacingOnChara(rotation)) _sentRotation = rotation;
    }

    /// <summary>モジュールが切れていれば読み込み、立ち上がったら Locked に進める。</summary>
    /// <remarks>LoadModule は「読み込みを始めた」だけで、IPC が生えるのは少し後。
    /// 立ち上がるまで毎フレーム IsModuleEnabled を見る。読み込めなかったら dry run に落とす。</remarks>
    private void AdvanceLoading()
    {
        if (_dr.IsModuleEnabled(FaceModule) == true)
        {
            _state = State.Locked;
            Log("モジュールが立ち上がった。固定を始める");
            return;
        }
        if (_loadRequested) return;

        _loadRequested = true;
        _loadedByUs = _dr.LoadModule(FaceModule);
        if (_loadedByUs) return;

        _dryRun = true;
        _state = State.Locked;
        _note = "LoadModule に失敗。dry run に落とした";
        Log(_note);
    }

    /// <summary>sources を発生源として、今フレームに向くべき方向をキャラの Rotation (ラジアン) で返す。</summary>
    /// <remarks>
    /// 発生源のうち嘘 (見る) が 1 人でも居れば、嘘の全員への方向の二等分線を向く。
    /// 全員が本物 (見ない) なら、本物の全員への二等分線の反対を向く。自分は発生源から除く。
    /// 保持者なら発生源はもう 1 人だけになり、そのまま「その人を向く/背を向ける」になる。
    ///
    /// 二等分線は方向を単位ベクトルにしてから足して作る。生のベクトルを足すと位置の中点を
    /// 向くことになり、2 人の距離が違うと遠い方に引っ張られて近い方が扇から外れる。
    ///
    /// 2 人がちょうど反対側に居ると単位ベクトルが打ち消し合って向きが決まらない。そのとき
    ///   見ない → 片方に直角を向く。両方 ±90° になり、±45° の扇の外に出る
    ///   見る   → 両方を見るのは不可能。近い方だけでも見る
    ///
    /// Rotation は atan2(ΔX, ΔZ)。Daily Routines の WorldDirHToChara と同じ式で、
    /// 0=南 / +π/2=東 / ±π=北。方位 (度) を一度も経由しない。
    /// </remarks>
    private float? ComputeFacing(List<WaveSource> sources)
    {
        var me = BasePlayer!;
        var myPos = new Vector2(me.Position.X, me.Position.Z);
        Vector2 lookSum = default, awaySum = default, nearestLook = default, firstAway = default;
        var lookCount = 0;
        var awayCount = 0;
        var nearestDistance = float.MaxValue;

        foreach (var src in sources)
        {
            src.IsSelf = src.EntityId == me.EntityId;
            src.Present = false;
            if (src.IsSelf || src.EntityId.GetObject() is not { } obj) continue;

            var delta = new Vector2(obj.Position.X, obj.Position.Z) - myPos;
            var distance = delta.Length();
            if (distance < 0.01f) continue;   // 重なっていると方向が決まらない

            src.Present = true;
            src.Unit = delta / distance;
            if (src.Fake)
            {
                lookSum += src.Unit;
                lookCount++;
                if (distance < nearestDistance) { nearestDistance = distance; nearestLook = src.Unit; }
            }
            else
            {
                awaySum += src.Unit;
                if (awayCount++ == 0) firstAway = src.Unit;
            }
        }

        Vector2 facing;
        if (lookCount > 0)
            facing = lookSum.LengthSquared() > 0.0001f ? lookSum : nearestLook;
        else if (awayCount > 0)
            facing = awaySum.LengthSquared() > 0.0001f ? -awaySum : new Vector2(firstAway.Y, -firstAway.X);
        else
            return null;

        facing = Vector2.Normalize(facing);
        var actual = FacingOf(me.Rotation);
        foreach (var src in sources.Where(x => x.Present))
        {
            src.PlannedAngle = AngleBetween(facing, src.Unit);
            src.ActualAngle = AngleBetween(actual, src.Unit);
        }
        return MathF.Atan2(facing.X, facing.Y);
    }

    /// <summary>2 つの単位ベクトルの間の角度 (度、0〜180)。</summary>
    private static float AngleBetween(Vector2 a, Vector2 b) =>
        MathF.Acos(Math.Clamp(Vector2.Dot(a, b), -1f, 1f)) * 180f / MathF.PI;

    /// <summary>見る/見ないの要求を満たしているか。</summary>
    /// <remarks>扇は ±45°。P4_Debuff_Reminder の EyeScope 要素 (coneAngleMin -45 / Max 45) に合わせてある。
    /// 嘘なら扇の中、本物なら扇の外に居れば OK。</remarks>
    private static bool Satisfies(WaveSource src, float angle) => (angle <= ConeHalfAngle) == src.Fake;

    /// <summary>Rotation から (X, Z) の単位ベクトルに戻す。0=南 → (0, +1)。</summary>
    /// <remarks>画面の右=東 / 下=南 に置けば、そのまま画面上の向きにもなる (Debug のレーダー)。</remarks>
    private static Vector2 FacingOf(float rotation) => new(MathF.Sin(rotation), MathF.Cos(rotation));

    /// <summary>発動の瞬間の判定を取る。見たか/見なかったかの答え合わせ用。</summary>
    /// <remarks>ここでの「実際」は自分の Rotation から計算したもの。ゲーム側の扇の幅は未確認
    /// (±45° は P4_Debuff_Reminder の表示に合わせた仮定) なので、NG が境界付近ならそちらを疑う。</remarks>
    private void RecordFire()
    {
        _fired = true;
        _lastFire.Clear();
        foreach (var src in _wave)
        {
            var who = $"{src.Name} ({(src.Fake ? "嘘" : "本物")})";
            if (src.IsSelf) { _lastFire.Add(new($"{who}: 自分", null)); continue; }
            if (!src.Present) { _lastFire.Add(new($"{who}: 見えない", null)); continue; }
            var ok = Satisfies(src, src.ActualAngle);
            _lastFire.Add(new($"{who}: 実際 {src.ActualAngle:F1}° {(ok ? "OK" : "NG")} / 計画 {src.PlannedAngle:F1}°", ok));
        }
        _lastFireHeader = $"{DateTime.Now:HH:mm:ss.fff}  dry={_dryRun}  詠唱中={(BasePlayer?.IsCasting == true ? "Y" : "N")}  " +
                          $"計画 {Deg(_plannedRotation)} / 送信 {Deg(_sentRotation)} / 実際 {Deg(BasePlayer?.Rotation)}";
        Log($"発動: {_lastFireHeader} / " + string.Join(" / ", _lastFire.Select(x => x.Text)));
    }

    /// <summary>レーダーに出すものを作り直す。波をつかんでいればその波、まだなら次の波を仮に組む。</summary>
    /// <remarks>仮組みは TryLatchWave と同じ規則 (先頭から GroupSeconds 以内) で、真偽も今の記録から引く。
    /// 本物/嘘はデバフが付いた時点で確定しているので、波をつかむ前から計画の扇まで出せる。</remarks>
    private void UpdateRadar()
    {
        _radarLater.Clear();
        if (_state != State.None)
        {
            _radarPlanned = _plannedRotation;
            _radarCountdown = (_waveEndMs - Environment.TickCount64) / 1000f;
            foreach (var h in _holders.Where(h => _wave.All(w => w.EntityId != h.EntityId)))
                _radarLater.Add((h.EntityId, h.Remaining));
            return;
        }
        BuildPreview();
        _radarPlanned = _preview.Count > 0 && BasePlayer != null ? ComputeFacing(_preview) : null;
    }

    /// <summary>次に切れる組を _preview に仮に組み、残りを _radarLater に入れる。</summary>
    private void BuildPreview()
    {
        _preview.Clear();
        _radarCountdown = null;
        if (_holders.Count == 0) return;

        var first = _holders.Min(h => h.Remaining);
        _radarCountdown = first;
        foreach (var h in _holders)
        {
            if (h.Remaining > first + GroupSeconds) { _radarLater.Add((h.EntityId, h.Remaining)); continue; }
            _preview.Add(new WaveSource
            {
                EntityId = h.EntityId,
                Name = h.Name,
                Fake = _fakeStatuses.Contains((h.EntityId, h.StatusId)),
                RemainingAtLatch = h.Remaining,
            });
        }
    }

    /// <summary>固定を解き、読み込んだモジュールを戻して、波を捨てる。</summary>
    /// <remarks>どこから呼ばれても安全なようにしてある (OnReset / OnDisable / 死亡 / 時間切れ)。
    /// 固定したまま放置すると、Daily Routines が向きを握り続けてキャラが振り向けなくなる。</remarks>
    private void Finish(string reason)
    {
        if (_state == State.None) return;

        if (!_dryRun) _dr.CancelFacingLock();
        if (_loadedByUs && C.UnloadAfter) _dr.UnloadModule(FaceModule);
        Log($"波を終えた ({reason})。unload={_loadedByUs && C.UnloadAfter}");

        _state = State.None;
        _wave.Clear();
        _loadRequested = false;
        _loadedByUs = false;
        _sentRotation = null;
        _plannedRotation = null;
    }

    /// <summary>発動前後のスナップショットを始める。同じ波なら発動時刻だけ合わせ直す。</summary>
    /// <remarks>TryLatchWave (先頭の残りが 2 秒を切った時点) と BeginWave の両方から呼ぶ。
    /// TryLatchWave 側は固定を始める前から「実際」を取るため、BeginWave 側はテスト波のため。
    /// 発動時刻は TryLatchWave が毎フレーム残り秒数から推定し直すのでぶれる。1.5 秒以内は同じ波とみなす。
    /// 合わせ直すのは BeginWave から来たとき (align) だけ。波をつかんだ時点の値が RecordFire と同じ基準だから。
    /// TryLatchWave 側で合わせ直すと、発動直後に残り 0 秒付近で見えるデバフが発動時刻を後ろへずらしてしまう。</remarks>
    private void BeginSnapshot(long fireMs, bool align)
    {
        if (Math.Abs(fireMs - _lastSnapFireMs) < 1500) return;
        if (_snapFireMs != 0 && Math.Abs(fireMs - _snapFireMs) < 1500)
        {
            if (align) _snapFireMs = fireMs;
            return;
        }

        FlushSnapshot("次の波が来た");
        _samples.Clear();
        _snapFireMs = fireMs;
        _lastSampleMs = 0;
        _snapFireMarked = false;
        _snapHeader = "";
    }

    /// <summary>スナップショットを 1 行取る。発動 + SnapshotWindowMs を過ぎたら書き出す。</summary>
    /// <remarks>波を終えた後 (HoldSeconds 経過) も取り続ける。計画/送信が "-" になり、固定が外れたあとの
    /// 「実際」が見える。</remarks>
    private void SampleSnapshot()
    {
        if (_snapFireMs == 0) return;
        var now = Environment.TickCount64;
        if (now > _snapFireMs + SnapshotWindowMs) { FlushSnapshot("完了"); return; }

        var fire = !_snapFireMarked && now >= _snapFireMs;
        if (!fire && now - _lastSampleMs < SnapshotIntervalMs) return;
        if (BasePlayer is not { } me) return;

        _snapFireMarked |= fire;
        _lastSampleMs = now;
        _samples.Add(new(now, _state, _plannedRotation, _sentRotation, me.Rotation, me.IsCasting, fire));
    }

    /// <summary>取ったスナップショットを履歴 (VerboseLog なら dalamud.log にも) へ 1 件で書き出す。</summary>
    private void FlushSnapshot(string reason)
    {
        if (_snapFireMs == 0) return;
        var fireMs = _snapFireMs;
        _snapFireMs = 0;
        _lastSnapFireMs = fireMs;

        var fireAt = DateTime.Now.AddMilliseconds(fireMs - Environment.TickCount64);
        _lastSnapshot = $"発動前後のスナップショット: 発動 {fireAt:HH:mm:ss.fff} ({reason}, {_samples.Count} 行) {_snapHeader}\n" +
                        string.Join("\n", _samples.Select(x => FormatSample(x, fireMs)));
        _samples.Clear();
        Log(_lastSnapshot);
    }

    /// <summary>スナップショット 1 行。時刻は発動からの差 (秒)、Δ計画 = 実際 − 計画。</summary>
    private static string FormatSample(Sample x, long fireMs) =>
        $"  {(x.Tick - fireMs) / 1000f,6:+0.00;-0.00;0.00}s  {x.State,-7}  計画 {Deg(x.Planned),7}  送信 {Deg(x.Sent),7}  " +
        $"実際 {Deg(x.Actual),7}  Δ計画 {Delta(x.Actual, x.Planned),7}  詠唱 {(x.Casting ? "Y" : "N")}{(x.Fire ? "  ◀ 発動" : "")}";

    /// <summary>実際 − 目標 を (-180, 180] の度で。目標が無ければ "-"。</summary>
    private static string Delta(float actual, float? target) =>
        target is { } t ? $"{MathF.IEEERemainder(actual - t, MathF.Tau) * 180f / MathF.PI:+0.0;-0.0;0.0}°" : "-";

    /// <summary>ユーザ向けの設定。</summary>
    private void DrawSettings()
    {
        var available = _dr.Available;
        ImGuiEx.Text(available ? EColor.GreenBright : EColor.RedBright,
            available ? $"Daily Routines v{_dr.PluginVersion}" : "Daily Routines が読み込まれていない (計算だけして送らない)");

        ImGui.SetNextItemWidth(150f);
        ImGui.SliderFloat("Lock before (s)", ref C.LeadSeconds, 0.5f, 5f, "%.1f");
        ImGuiEx.Tooltip("視線の何秒前から向きを固定するか。モジュールの読み込みに約 0.5 秒かかる");
        ImGui.SetNextItemWidth(150f);
        ImGui.SliderFloat("Hold after (s)", ref C.HoldSeconds, 0f, 3f, "%.1f");
        ImGuiEx.Tooltip("視線の何秒後まで固定を続けるか");
        ImGui.Checkbox("Unload AutoFaceCameraDirection after", ref C.UnloadAfter);
        ImGuiEx.Tooltip("自分で読み込んだときだけ戻す。元から有効にしていたなら触らない");
        ImGui.Checkbox("Verbose log", ref C.VerboseLog);
        ImGuiEx.Text(EColor.YellowBright, "発動の瞬間に詠唱していると向きが送られない (モジュールの仕様)");
    }

    // ---- Debug: 現在 ---------------------------------------------------------

    /// <summary>今の波と、計画/実際の向きがそれぞれ要求を満たしているか。</summary>
    /// <remarks>計画が OK なのに実際が NG なら、固定が効いていない (モジュール未読込・詠唱中など)。</remarks>
    private void DrawNowTab()
    {
        ImGuiEx.Text($"State: {_state}  dry={_dryRun}  loadedByUs={_loadedByUs}  {FaceModule}: {ModuleText()}");
        ImGuiEx.Text($"発動まで: {(_state == State.None ? "-" : $"{(_waveEndMs - Environment.TickCount64) / 1000f:F2}s")}" +
                     $"  詠唱中: {(BasePlayer?.IsCasting == true ? "Y" : "N")}");
        ImGuiEx.Text($"計画: {Deg(_plannedRotation)}  送信済み: {Deg(_sentRotation)}  実際: {Deg(BasePlayer?.Rotation)}");
        if (_note != "") ImGuiEx.Text(EColor.YellowBright, _note);

        List<ImGuiEx.EzTableEntry> entries = [];
        foreach (var src in _wave)
        {
            entries.Add(new("Name", true, () => ImGuiEx.Text(src.Name + (src.IsSelf ? " (自分)" : ""))));
            entries.Add(new("真偽", () => ImGuiEx.Text(src.Fake ? "嘘 = 見る" : "本物 = 見ない")));
            entries.Add(new("残り(latch)", () => ImGuiEx.Text($"{src.RemainingAtLatch:F2}")));
            entries.Add(new("計画", () => ShowJudge(src, src.PlannedAngle)));
            entries.Add(new("実際", () => ShowJudge(src, src.ActualAngle)));
        }
        if (entries.Count > 0) ImGuiEx.EzTable(entries);
        if (BasePlayer == null) ImGuiEx.Text("レーダー: 自分が居ない");
        else DrawRadarCanvas(240f, 1f, false);
        ImGuiEx.Text($"緑=嘘 (見る) / 赤=本物 (見ない) / 灰=後の組 / 水色=計画の扇 ±{ConeHalfAngle:F0}° / 黄=実際の向き");
        DrawLastFire();
        DrawSnapshot();
    }

    private static void ShowJudge(WaveSource src, float angle)
    {
        if (src.IsSelf) { ImGuiEx.Text("-"); return; }
        if (!src.Present) { ImGuiEx.Text(EColor.RedBright, "見えない"); return; }
        var ok = Satisfies(src, angle);
        ImGuiEx.Text(ok ? EColor.GreenBright : EColor.RedBright, $"{angle:F1}° {(ok ? "OK" : "NG")}");
    }

    /// <summary>真上から見た図。北が上、自分が中心。Debug の「現在」とレーダーのウィンドウで共用する。</summary>
    /// <remarks>ゲームの X=東 / Z=南 を画面の右 / 下にそのまま置く。だから Z を反転しない。
    /// 縮尺は一番遠い点が外周の 9 割に来るように合わせる (最低 10m)。色の不透明度は全部 opacity 倍する。
    /// dummy なら DummyPoints を出す (デザインモード)。黄色の線 (実際の向き) だけは本物の自分の向き。</remarks>
    private void DrawRadarCanvas(float size, float opacity, bool dummy)
    {
        if (BasePlayer is not { } me) return;
        IReadOnlyList<RadarPoint> points = dummy ? DummyPoints : CollectRadarPoints(new Vector2(me.Position.X, me.Position.Z), me.EntityId);
        var planned = dummy ? DummyPlanned() : _radarPlanned;
        var range = MathF.Max(10f, points.Count == 0 ? 0f : points.Max(p => p.Offset.Length()) / 0.9f);
        var origin = ImGui.GetCursorScreenPos();
        var center = origin + new Vector2(size / 2f);
        var radius = size / 2f - 14f;
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(origin, origin + new Vector2(size), Col(EColor.Black, 0.6f * opacity), 6f);
        dl.AddCircle(center, radius, Col(EColor.White, 0.3f * opacity));
        dl.AddText(center + new Vector2(-4f, -radius - 13f), Col(EColor.White, opacity), "N");
        dl.AddText(origin + new Vector2(6f, size - 18f), Col(EColor.White, 0.5f * opacity), $"{range:F0}m");
        if (planned is { } r) DrawCone(dl, center, radius, r, opacity);
        dl.AddLine(center, center + FacingOf(me.Rotation) * radius, Col(EColor.YellowBright, opacity), 2f);

        DrawRadarPoints(dl, center, radius / range, points, opacity);
        DrawRadarCaption(dl, origin + new Vector2(6f, 4f), points, dummy ? 3.2f : _radarCountdown, dummy, opacity);
        if (dummy) DrawDesignFrame(dl, origin, size);
        ImGui.Dummy(new Vector2(size));
    }

    /// <summary>レーダーに出す点を今の状態から集める。毎フレーム同じリストを使い回す。</summary>
    private List<RadarPoint> CollectRadarPoints(Vector2 myPos, uint myId)
    {
        Vector2 Offset(Vector3 p) => new(p.X - myPos.X, p.Z - myPos.Y);

        _radarPoints.Clear();
        foreach (var (id, remaining) in _radarLater)
            if (id.GetObject() is { } o)
                _radarPoints.Add(new(Offset(o.Position), false, true, false, $"{remaining:F0}s"));
        foreach (var src in RadarSources)
        {
            if (src.EntityId == myId) { _radarPoints.Add(new(Vector2.Zero, src.Fake, false, true, "")); continue; }
            if (src.EntityId.GetObject() is { } o)
                _radarPoints.Add(new(Offset(o.Position), src.Fake, false, false, src.Name.Length > 8 ? src.Name[..8] : src.Name));
        }
        return _radarPoints;
    }

    /// <summary>ダミーの計画。次の組 (Later でない点) への二等分線。ComputeFacing の「見る」と同じ作り。</summary>
    private static float DummyPlanned()
    {
        var sum = DummyPoints.Where(p => !p.Later).Aggregate(Vector2.Zero, (a, p) => a + Vector2.Normalize(p.Offset));
        return MathF.Atan2(sum.X, sum.Y);
    }

    /// <summary>点を描く。後の組は灰色に残り秒数、次の組は緑 (嘘) / 赤 (本物)、自分が保持者なら中心に輪。</summary>
    private static void DrawRadarPoints(ImDrawListPtr dl, Vector2 center, float scale, IReadOnlyList<RadarPoint> points, float opacity)
    {
        foreach (var p in points.Where(x => x.Later))
        {
            var pos = center + p.Offset * scale;
            dl.AddCircleFilled(pos, 4f, Col(EColor.White, 0.35f * opacity));
            dl.AddText(pos + new Vector2(6f, -7f), Col(EColor.White, 0.5f * opacity), p.Label);
        }
        foreach (var p in points.Where(x => !x.Later))
        {
            var color = Col(p.Fake ? EColor.GreenBright : EColor.RedBright, opacity);
            if (p.IsSelf) { dl.AddCircle(center, 9f, color, 0, 2f); continue; }
            var pos = center + p.Offset * scale;
            dl.AddCircleFilled(pos, 5f, color);
            dl.AddText(pos + new Vector2(7f, -7f), Col(EColor.White, opacity), p.Label);
        }
        dl.AddCircleFilled(center, 4f, Col(EColor.White, opacity));
    }

    /// <summary>左上の文字。次の発動まで何秒か、見るのか見ないのか、固定中か。</summary>
    /// <remarks>見る/見ないは自分以外の発生源で決める (自分の視線は自分に当たらない)。</remarks>
    private void DrawRadarCaption(ImDrawListPtr dl, Vector2 pos, IReadOnlyList<RadarPoint> points, float? countdown, bool dummy, float opacity)
    {
        var sources = points.Where(x => !x.Later && !x.IsSelf).ToList();
        if (countdown is not { } seconds || sources.Count == 0) return;
        var look = sources.Any(x => x.Fake);
        var mode = dummy ? "  デザイン" : _state == State.None ? "" : _dryRun ? "  計算のみ" : "  固定中";
        dl.AddText(pos, Col(look ? EColor.GreenBright : EColor.RedBright, opacity),
            $"{MathF.Max(seconds, 0f):F1}s  {(look ? "見る" : "見ない")}{mode}");
    }

    /// <summary>デザインモードの印。動かせることが分かるように黄色の枠と案内を出す。不透明度は掛けない。</summary>
    private static void DrawDesignFrame(ImDrawListPtr dl, Vector2 origin, float size)
    {
        dl.AddRect(origin, origin + new Vector2(size), EColor.YellowBright.ToUint(), 6f, ImDrawFlags.None, 2f);
        const string hint = "ドラッグで移動";
        dl.AddText(origin + new Vector2(size - ImGui.CalcTextSize(hint).X - 6f, size - 18f), EColor.YellowBright.ToUint(), hint);
    }

    /// <summary>計画の向きの扇 (±ConeHalfAngle) を塗る。</summary>
    /// <remarks>ImGui の弧の角度は画面の +x から +y (下) へ回る。FacingOf を画面にそのまま置いているので、
    /// 中心角は atan2(画面 y, 画面 x)。</remarks>
    private static void DrawCone(ImDrawListPtr dl, Vector2 center, float radius, float rotation, float opacity)
    {
        var f = FacingOf(rotation);
        var mid = MathF.Atan2(f.Y, f.X);
        var half = ConeHalfAngle * MathF.PI / 180f;
        dl.PathLineTo(center);
        dl.PathArcTo(center, radius, mid - half, mid + half, 24);
        dl.PathFillConvex(Col(EColor.CyanBright, 0.25f * opacity));
    }

    /// <summary>色に不透明度を掛けて ImGui の色にする。</summary>
    private static uint Col(Vector4 color, float alpha) => (color with { W = color.W * alpha }).ToUint();

    /// <summary>直前の発動の瞬間の判定。波が終わっても次の発動まで残す。</summary>
    private void DrawLastFire()
    {
        ImGui.Separator();
        if (_lastFire.Count == 0) { ImGuiEx.Text("前回の発動: まだ無い"); return; }
        ImGuiEx.Text($"前回の発動: {_lastFireHeader}");
        foreach (var line in _lastFire)
            ImGuiEx.Text(line.Ok switch { true => EColor.GreenBright, false => EColor.RedBright, null => EColor.White },
                "  " + line.Text);
    }

    /// <summary>直前に書き出したスナップショット。履歴タブにも同じものが入る。</summary>
    private void DrawSnapshot()
    {
        var recording = _snapFireMs != 0 ? $"  記録中 {_samples.Count} 行" : "";
        if (!ImGui.TreeNode($"発動前後のスナップショット ±{SnapshotWindowMs / 1000f:F0}s{recording}###P4LFSnap")) return;
        if (_lastSnapshot == "")
        {
            ImGuiEx.Text("まだ無い");
        }
        else
        {
            if (ImGui.Button("コピー##snap")) GenericHelpers.Copy(_lastSnapshot);
            ImGuiEx.Text(_lastSnapshot);
        }
        ImGui.TreePop();
    }

    // ---- Debug: レーダー -------------------------------------------------------

    /// <summary>別ウィンドウのレーダーの設定と位置合わせ。</summary>
    /// <remarks>デザインモードの間だけダミーを出し、ドラッグで動かせる。その間はレーダーの上のクリックが
    /// ゲームに届かない。設定画面を閉じるとデザインモードは切れ、クリックを素通りさせる状態に戻る。</remarks>
    private void DrawRadarTab()
    {
        ImGui.Checkbox("デバフが付いている間レーダーを出す", ref C.ShowRadar);
        ImGui.SetNextItemWidth(150f);
        ImGui.SliderFloat("不透明度", ref C.RadarOpacity, 0.1f, 1f, "%.2f");
        ImGui.SetNextItemWidth(150f);
        ImGui.SliderFloat("大きさ (px)", ref C.RadarSize, 120f, 480f, "%.0f");

        if (ImGui.Checkbox("デザインモード (ダミーを出してドラッグで動かす)", ref _radarDesign) && _radarDesign)
            EnsureRadarWindow();
        ImGuiEx.Tooltip("設定画面を閉じると切れる。普段のレーダーはクリックを素通りさせ、動かせない");
        ImGuiEx.Text($"位置: {C.RadarPosition.X:F0}, {C.RadarPosition.Y:F0}");
        ImGui.SameLine();
        if (ImGui.Button("初期位置に戻す"))
        {
            C.RadarPosition = DefaultRadarPosition;
            _radarForcePosition = true;
        }
    }

    /// <summary>レーダーのウィンドウが無ければ作る。</summary>
    /// <remarks>OnEnable と、デザインモードを入れたときに呼ぶ。後者は Dancing Mad の外 (スクリプトが無効) でも
    /// 位置合わせできるようにするため。外は OnDisable が担う。無効のまま作ったものは OnDisable が来ないので残るが、
    /// DesignActive が切れれば何も出さない。</remarks>
    private void EnsureRadarWindow() => _radarWindow ??= new RadarWindow(this);

    // ---- Debug: 記録 ---------------------------------------------------------

    /// <summary>本物/嘘の記録と、今デバフを持っている人。</summary>
    private void DrawRecordTab()
    {
        ImGuiEx.Text($"フェーズ中: {PhaseActive}  今の回: {(_isLie ? "嘘つき" : "正直")}  記録済みの嘘: {_fakeStatuses.Count} 件");
        ImGuiEx.Text("分身:");
        if (_isTruth.Count == 0) ImGuiEx.Text("  (まだ無い)");
        foreach (var (id, truth) in _isTruth)
            ImGuiEx.Text(truth ? EColor.GreenBright : EColor.OrangeBright,
                $"  0x{id:X8} {(truth ? "正直" : "嘘つき")}  {(id.GetObject() is { } o ? $"BaseId={o.BaseId}" : "(消えた)")}");

        ImGuiEx.Text("嘘の回に付いた呪詛の叫声:");
        if (_fakeStatuses.Count == 0) ImGuiEx.Text("  (まだ無い)");
        foreach (var (id, status) in _fakeStatuses)
            ImGuiEx.Text($"  {NameOf(id)}  status={status}");
        DrawHolders();
    }

    /// <summary>今デバフを持っている人。判定は「今つかんだらこう判定する」もの。</summary>
    /// <remarks>先頭の残りが LeadSeconds を切った瞬間に、先頭から 2 秒以内の人が 1 つの波になる。</remarks>
    private void DrawHolders()
    {
        ImGuiEx.Text($"今の保持者 (残り {C.LeadSeconds:F1}s で波になる):");
        List<ImGuiEx.EzTableEntry> entries = [];
        foreach (var pc in Controller.GetPartyMembers())
        {
            var s = pc.StatusList.FirstOrDefault(x => x != null && LookAwayIds.Contains(x.StatusId) && x.RemainingTime > 0f);
            if (s == null) continue;
            var name = pc.Name.ToString();
            var remaining = s.RemainingTime;
            var fake = _fakeStatuses.Contains((pc.EntityId, s.StatusId));
            entries.Add(new("Name", true, () => ImGuiEx.Text(name)));
            entries.Add(new("残り", () => ImGuiEx.Text($"{remaining:F1}s")));
            entries.Add(new("判定", () => ImGuiEx.Text(fake ? EColor.GreenBright : EColor.RedBright, fake ? "嘘 = 見る" : "本物 = 見ない")));
        }
        if (entries.Count == 0) ImGuiEx.Text("  (居ない)");
        else ImGuiEx.EzTable(entries);
    }

    // ---- Debug: テスト ------------------------------------------------------

    /// <summary>好きな人を発生源にしたテスト波と、Daily Routines の手動操作。</summary>
    /// <remarks>テスト波は本物の波と同じ道 (BeginWave → Advance) を通る。OnUpdate が回っている
    /// (= このエリアでスクリプトが有効な) ときしか進まない。</remarks>
    private void DrawTestTab()
    {
        if (!IsEnabled) ImGuiEx.Text(EColor.RedBright, "スクリプトが有効でない (Dancing Mad の外)。テスト波は進まない");
        DrawTestSources();
        ImGui.SetNextItemWidth(150f);
        ImGui.SliderFloat("発動まで (s)", ref _testFireSeconds, 0.5f, 10f, "%.1f");
        ImGui.Checkbox("dry run (送らずに計算だけ)", ref _testDryRun);

        var picked = _testRole.Any(x => x.Value != 0);
        ImGui.BeginDisabled(_state != State.None || !picked || !IsEnabled || BasePlayer == null);
        if (ImGui.Button("テスト波を始める")) StartTestWave();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(_state == State.None);
        if (ImGui.Button("中止")) Finish("手動で中止");
        ImGui.EndDisabled();

        ImGui.Separator();
        DrawManualControls();
    }

    /// <summary>発生源の候補 = パーティ + 今のターゲット。人ごとに 対象外 / 本物 / 嘘 を選ぶ。</summary>
    private void DrawTestSources()
    {
        var candidates = Controller.GetPartyMembers().Select(x => (x.EntityId, Name: x.Name.ToString())).ToList();
        if (Svc.Targets.Target is { } t && candidates.All(x => x.EntityId != t.EntityId))
            candidates.Add((t.EntityId, t.Name.ToString()));

        foreach (var (id, name) in candidates)
        {
            var role = _testRole.GetValueOrDefault(id);
            ImGui.RadioButton($"-##{id}", ref role, 0);
            ImGui.SameLine();
            ImGui.RadioButton($"本物##{id}", ref role, 1);
            ImGui.SameLine();
            ImGui.RadioButton($"嘘##{id}", ref role, 2);
            ImGui.SameLine();
            ImGuiEx.Text(id == BasePlayer?.EntityId ? $"{name} (自分)" : name);
            _testRole[id] = role;
        }
    }

    /// <summary>選んだ発生源で、_testFireSeconds 後に発動する波を始める。</summary>
    private void StartTestWave()
    {
        _wave.Clear();
        foreach (var (id, role) in _testRole)
        {
            if (role == 0 || id.GetObject() is not { } obj) continue;
            _wave.Add(new WaveSource { EntityId = id, Name = obj.Name.ToString(), Fake = role == 2, RemainingAtLatch = _testFireSeconds });
        }
        if (_wave.Count == 0) return;
        BeginWave(Environment.TickCount64 + (long)(_testFireSeconds * 1000f), _testDryRun, "テスト波");
    }

    /// <summary>Daily Routines を直接叩くボタン。角度の規約を実機で確かめるため。</summary>
    /// <remarks>固定は「固定を解除」を押すまで残る。波の最中は波の処理と取り合うので押せなくしてある。</remarks>
    private void DrawManualControls()
    {
        ImGuiEx.Text($"Daily Routines の手動操作 ({FaceModule}: {ModuleText()})");
        ImGui.BeginDisabled(_state != State.None);
        if (ImGui.Button("読み込む")) Manual("LoadModule", _dr.LoadModule(FaceModule));
        ImGui.SameLine();
        if (ImGui.Button("戻す")) Manual("UnloadModule", _dr.UnloadModule(FaceModule));
        ImGui.SameLine();
        if (ImGui.Button("固定を解除")) Manual("CancelFacingLock", _dr.CancelFacingLock());

        foreach (var (label, bearing) in Bearings)
        {
            if (ImGui.Button($"{label}を向く")) Manual($"LockFacingOnBearing({bearing})", _dr.LockFacingOnBearing(bearing));
            ImGui.SameLine();
        }
        if (ImGui.Button("ターゲットを向く") && BasePlayer is { } me && Svc.Targets.Target is { } t)
            Manual($"LockFacingToward({t.Name})", _dr.LockFacingToward(me.Position, t.Position));
        ImGui.EndDisabled();
    }

    private void Manual(string what, bool sent)
    {
        _note = $"手動: {what} → {(sent ? "送った" : "送れなかった (モジュール無効 / リプレイ中 / Daily Routines 無し)")}";
        Log(_note);
    }

    // ---- Debug: 履歴 ---------------------------------------------------------

    /// <summary>Log の履歴。VerboseLog が切れていても残す。新しい順。</summary>
    private void DrawHistoryTab()
    {
        if (ImGui.Button("コピー")) GenericHelpers.Copy(string.Join("\n", _history));
        ImGui.SameLine();
        if (ImGui.Button("消去")) _history.Clear();
        ImGui.SameLine();
        ImGuiEx.Text($"{_history.Count}/{HistoryMax} 行");
        for (var i = _history.Count - 1; i >= 0; i--) ImGuiEx.TextWrapped(_history[i]);
    }

    // ---- 小物 -----------------------------------------------------------------

    private string ModuleText() => _dr.IsModuleEnabled(FaceModule) switch { true => "ON", false => "OFF", null => "不明" };

    private static string NameOf(uint entityId) => entityId.GetObject()?.Name.ToString() ?? $"0x{entityId:X8}";

    /// <summary>Rotation (ラジアン) を度で出す。null は "-"。</summary>
    private static string Deg(float? rotation) => rotation is { } r ? $"{r * 180f / MathF.PI:F1}°" : "-";

    /// <summary>判断を履歴に残す。VerboseLog なら dalamud.log にも出す。</summary>
    private void Log(string message)
    {
        _history.Add($"{DateTime.Now:HH:mm:ss.fff} [{_state}] {message}");
        if (_history.Count > HistoryMax) _history.RemoveRange(0, _history.Count - HistoryMax);
        if (C.VerboseLog) PluginLog.Information($"[P4LF {_state}] {message}");
    }

    #endregion
}
