using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using ECommons.Hooks.ActionEffectTypes;
using ECommons.ImGuiMethods;
using ECommons.Logging;
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

        /// <summary>購読を張る。OnSetup か OnEnable から 1 回呼ぶ。</summary>
        /// <remarks>2 回呼んでも購読が重複するだけで害は無いが、無駄なので閂を掛けてある。
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

    #endregion

    #region public properties
    /********************************************************************/
    /* public properties                                                */
    /********************************************************************/
    public override HashSet<uint>? ValidTerritories { get; } = [1363];   // Dancing Mad (Ultimate)
    public override Metadata Metadata => new(1, "Redmoon");

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

    // ---- 波をまたいで残す。OnReset で戻す ------------------------------------
    private long _lastWaveEndMs;         // 直前に処理した波。切れかけのデバフでつかみ直さないため

    // 毎フレームの走査で使い回す。フェーズ中ずっと走るので確保しない
    private readonly List<(uint EntityId, string Name, uint StatusId, float Remaining)> _holders = [];

    /// <summary>ケフカ本体が居て殴れる = このフェーズの最中。</summary>
    private bool PhaseActive => Svc.Objects.Any(x => x.BaseId == 18475 && x.IsTargetable);

    #endregion

    #region public methods
    /********************************************************************/
    /* public methods                                                   */
    /********************************************************************/
    // 本物/嘘の記録は P4_Debuff_Reminder の OnVFXSpawn / OnActionEffectEvent / OnGainBuffEffect を
    // 呪詛の叫声だけに絞って写したもの。向きの判断は OnUpdate から private に出してある。

    public override void OnEnable() => _dr.Init();

    public override void OnDisable() => Finish("disable");

    public override void OnReset()
    {
        Finish("reset");
        _isTruth.Clear();
        _fakeStatuses.Clear();
        _lastWaveEndMs = 0;
    }

    public override void OnUpdate()
    {
        if (_state == State.None)
        {
            if (BasePlayer != null && PhaseActive) TryLatchWave();
            return;
        }
        Advance();
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
        DrawSettings();
        if (!ImGuiEx.CollapsingHeader("Debug")) return;
        DrawDebug();
    }

    #endregion

    #region private methods
    /********************************************************************/
    /* private methods                                                  */
    /********************************************************************/

    /// <summary>次の視線の波が近づいていたら、その波の保持者をつかむ。</summary>
    /// <remarks>一番早く切れる人の残りが LeadSeconds を切った瞬間に、その人から 2 秒以内に
    /// 切れる人を全員まとめて 1 つの波にする。短い組と長い組は約 9 秒離れているので混ざらない。
    /// 本物/嘘はつかんだ時点で確定させる。視線が撃たれるころにはデバフが消えていて引けないため。
    ///
    /// 直前に処理した波と同じ時刻に切れるものは拾わない。固定を解いた直後、消えかけのデバフが
    /// 残り 0 秒付近で数フレーム見えることがあるため。</remarks>
    private void TryLatchWave()
    {
        _holders.Clear();
        foreach (var pc in Controller.GetPartyMembers())
            foreach (var s in pc.StatusList)
            {
                if (!LookAwayIds.Contains(s.StatusId) || s.RemainingTime <= 0f) continue;
                _holders.Add((pc.EntityId, pc.Name.ToString(), s.StatusId, s.RemainingTime));
                break;
            }
        if (_holders.Count == 0) return;

        var first = _holders.Min(h => h.Remaining);
        if (first > C.LeadSeconds) return;
        var now = Environment.TickCount64;
        var endMs = now + (long)(first * 1000f);
        if (Math.Abs(endMs - _lastWaveEndMs) < 1500) return;

        _wave.Clear();
        foreach (var h in _holders.Where(h => h.Remaining <= first + 2f))
            _wave.Add(new WaveSource
            {
                EntityId = h.EntityId,
                Name = h.Name,
                Fake = _fakeStatuses.Contains((h.EntityId, h.StatusId)),
                RemainingAtLatch = h.Remaining,
            });

        _waveEndMs = endMs;
        _lastWaveEndMs = endMs;
        _loadRequested = false;
        _loadedByUs = false;
        _sentRotation = null;
        _dryRun = Svc.Condition[ConditionFlag.DutyRecorderPlayback] || !_dr.Available;
        _state = _dryRun || _dr.IsModuleEnabled("AutoFaceCameraDirection") == true ? State.Locked : State.Loading;
        _note = _dryRun ? "dry run (リプレイ中か Daily Routines が無い)" : "";
        Log($"波をつかんだ: 残り {first:F2}s / " +
            string.Join(", ", _wave.Select(w => $"{w.Name}={(w.Fake ? "嘘" : "本物")}")) +
            $" / state={_state} dry={_dryRun}");
    }

    /// <summary>つかんだ波を 1 フレーム進める。モジュールの読み込み → 向きの送信 → 終了判定。</summary>
    private void Advance()
    {
        var now = Environment.TickCount64;
        if (now > _waveEndMs + (long)(C.HoldSeconds * 1000f)) { Finish("発動から HoldSeconds 経過"); return; }
        if (BasePlayer == null || BasePlayer.IsDead) { Finish("自分が居ない/死んでいる"); return; }

        if (_state == State.Loading) AdvanceLoading();

        // 読み込み待ちでも計算はする。Debug で「何を向くつもりか」を先に見られるように
        _plannedRotation = ComputeFacing();
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
        if (_dr.IsModuleEnabled("AutoFaceCameraDirection") == true)
        {
            _state = State.Locked;
            Log("モジュールが立ち上がった。固定を始める");
            return;
        }
        if (_loadRequested) return;

        _loadRequested = true;
        _loadedByUs = _dr.LoadModule("AutoFaceCameraDirection");
        if (_loadedByUs) return;

        _dryRun = true;
        _state = State.Locked;
        _note = "LoadModule に失敗。dry run に落とした";
        Log(_note);
    }

    /// <summary>今フレームに向くべき方向を、キャラの Rotation (ラジアン) で返す。</summary>
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
    private float? ComputeFacing()
    {
        var me = BasePlayer!;
        var myPos = new Vector2(me.Position.X, me.Position.Z);
        Vector2 lookSum = default, awaySum = default, nearestLook = default, firstAway = default;
        var lookCount = 0;
        var awayCount = 0;
        var nearestDistance = float.MaxValue;

        foreach (var src in _wave)
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
        // 実際の向き。Rotation から (X, Z) の単位ベクトルに戻す (0=南 → (0, +1))
        var actual = new Vector2(MathF.Sin(me.Rotation), MathF.Cos(me.Rotation));
        foreach (var src in _wave.Where(x => x.Present))
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
    private static bool Satisfies(WaveSource src, float angle) => (angle <= 45f) == src.Fake;

    /// <summary>固定を解き、読み込んだモジュールを戻して、波を捨てる。</summary>
    /// <remarks>どこから呼ばれても安全なようにしてある (OnReset / OnDisable / 死亡 / 時間切れ)。
    /// 固定したまま放置すると、Daily Routines が向きを握り続けてキャラが振り向けなくなる。</remarks>
    private void Finish(string reason)
    {
        if (_state == State.None) return;

        if (!_dryRun) _dr.CancelFacingLock();
        if (_loadedByUs && C.UnloadAfter) _dr.UnloadModule("AutoFaceCameraDirection");
        Log($"波を終えた ({reason})。unload={_loadedByUs && C.UnloadAfter}");

        _state = State.None;
        _wave.Clear();
        _loadRequested = false;
        _loadedByUs = false;
        _sentRotation = null;
        _plannedRotation = null;
    }

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

    /// <summary>今の波と、計画/実際の向きがそれぞれ要求を満たしているか。</summary>
    /// <remarks>計画が OK なのに実際が NG なら、固定が効いていない (モジュール未読込・詠唱中など)。</remarks>
    private void DrawDebug()
    {
        ImGuiEx.Text($"State: {_state}  dry={_dryRun}  loadedByUs={_loadedByUs}  isLie={_isLie}");
        ImGuiEx.Text($"発動まで: {(_state == State.None ? "-" : $"{(_waveEndMs - Environment.TickCount64) / 1000f:F2}s")}");
        ImGuiEx.Text($"計画: {Deg(_plannedRotation)}  送信済み: {Deg(_sentRotation)}  " +
                     $"実際: {(BasePlayer is { } me ? $"{me.Rotation * 180f / MathF.PI:F1}°" : "-")}");
        if (_note != "") ImGuiEx.Text(EColor.YellowBright, _note);
        ImGuiEx.Text($"記録済みの嘘: {_fakeStatuses.Count} 件 / 分身: " +
                     string.Join(", ", _isTruth.Select(x => $"0x{x.Key:X}={(x.Value ? "正直" : "嘘つき")}")));

        List<ImGuiEx.EzTableEntry> entries = [];
        foreach (var src in _wave)
        {
            entries.Add(new("Name", true, () => ImGuiEx.Text(src.Name + (src.IsSelf ? " (自分)" : ""))));
            entries.Add(new("真偽", () => ImGuiEx.Text(src.Fake ? "嘘 = 見る" : "本物 = 見ない")));
            entries.Add(new("残り(latch)", () => ImGuiEx.Text($"{src.RemainingAtLatch:F2}")));
            entries.Add(new("計画", () => ShowJudge(src, src.PlannedAngle)));
            entries.Add(new("実際", () => ShowJudge(src, src.ActualAngle)));
        }
        ImGuiEx.EzTable(entries);
    }

    private static void ShowJudge(WaveSource src, float angle)
    {
        if (src.IsSelf) { ImGuiEx.Text("-"); return; }
        if (!src.Present) { ImGuiEx.Text(EColor.RedBright, "見えない"); return; }
        var ok = Satisfies(src, angle);
        ImGuiEx.Text(ok ? EColor.GreenBright : EColor.RedBright, $"{angle:F1}° {(ok ? "OK" : "NG")}");
    }

    /// <summary>Rotation (ラジアン) を度で出す。null は "-"。</summary>
    private static string Deg(float? rotation) => rotation is { } r ? $"{r * 180f / MathF.PI:F1}°" : "-";

    private void Log(string message)
    {
        if (!C.VerboseLog) return;
        PluginLog.Information($"[P4LF {_state}] {message}");
    }

    #endregion
}
