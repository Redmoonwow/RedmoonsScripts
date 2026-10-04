using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using ECommons.ImGuiMethods;
using Splatoon.SplatoonScripting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RedmoonsScripts.Generic;

/// <summary>
/// Daily Routines (AtmoOmen) の IPC を叩くためのラッパと、その動作確認用スクリプト。
/// </summary>
/// <remarks>
/// 本体は <see cref="Api"/>。スクリプト側はそれを動かして結果を見るだけの器。
///
/// Splatoon はスクリプトを 1 ファイルずつ別アセンブリにコンパイルする
/// (<c>ScriptingProcessor.CompileAndLoad</c> は 1 ソースしか受け取らず、
/// <c>ReferenceCache.BuildReferenceList</c> の参照集合にスクリプトのアセンブリは入らない)。
/// つまり他のスクリプトからこのクラスを参照することはできない。
/// <c>#region class</c> を丸ごとコピーして自分のスクリプトに貼る前提で書いてある。
///
/// 対象は Daily Routines 2.2.0.0。エンドポイント名は公式ドキュメントではなく配布 DLL 内の
/// 文字列リテラルから取った。ドキュメントとの差は 2 点:
///   ・AutoRefreshMarketSearchResult.IsMarketStuck は 2.2.0.0 に存在しないので入れていない
///   ・FastGrandCompanyExchange.EnqueueByName はドキュメントに無いが DLL にある (引数不明で未実装)
/// </remarks>
internal class DailyRoutinesIPC : SplatoonScript
{
    #region class
    /********************************************************************/
    /* class                                                            */
    /********************************************************************/

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

    #region public properties
    /********************************************************************/
    /* public properties                                                */
    /********************************************************************/
    public override HashSet<uint>? ValidTerritories { get; } = null;   // どこでも。OnUpdate は何もしない
    public override Metadata Metadata => new(3, "Redmoon");

    #endregion

    #region private properties
    /********************************************************************/
    /* private properties                                               */
    /********************************************************************/
    private readonly Api _dr = new();
    private bool _allowActions;                  // 副作用のあるボタンを押せるようにするか
    private string _moduleName = "AutoRepair";   // Debug で状態を見るモジュール
    private string _lastResult = "";             // 最後に押したボタンの戻り値

    #endregion

    #region public methods
    /********************************************************************/
    /* public methods                                                   */
    /********************************************************************/
    // ギミックには関与しない。IPC を張って、設定画面から叩けるようにするだけ。

    public override void OnSetup() => _dr.Init();

    public override void OnSettingsDraw()
    {
        DrawStatus();
        if (!ImGuiEx.CollapsingHeader("Debug")) return;

        DrawReadOnly();
        ImGui.Separator();
        DrawActions();
    }

    #endregion

    #region private methods
    /********************************************************************/
    /* private methods                                                  */
    /********************************************************************/
    // 読むだけのものと、ゲームに影響するものを分けてある。後者は CTRL を押していないと押せない。
    // 速度倍率や向き固定は実際にキャラが動くため。

    /// <summary>プラグインが居るか、指定したモジュールが有効かを出す。</summary>
    private void DrawStatus()
    {
        var available = _dr.Available;
        ImGuiEx.Text(available ? EColor.GreenBright : EColor.RedBright,
            available ? $"Daily Routines v{_dr.PluginVersion}" : "Daily Routines が読み込まれていない");
        if (!available) return;

        ImGui.SetNextItemWidth(200f);
        ImGui.InputText("Module name", ref _moduleName, 64);
        ImGui.SameLine();
        ImGuiEx.Text(_dr.IsModuleEnabled(_moduleName) switch
        {
            true => "有効",
            false => "無効",
            _ => "そんなモジュールは無い"
        });
    }

    /// <summary>状態を読むだけの IPC。再生中でも通る。</summary>
    private void DrawReadOnly()
    {
        ImGuiEx.Text("読み取り (null = プラグイン無し or モジュール無効)");
        ImGui.Indent();
        ImGuiEx.Text($"AutoRepair.IsBusy               = {Show(_dr.AutoRepairIsBusy)}");
        ImGuiEx.Text($"AutoRepair.IsNeedToRepair       = {Show(_dr.AutoRepairIsNeedToRepair)}");
        ImGuiEx.Text($"AutoRepair.IsAbleToRepair       = {Show(_dr.AutoRepairIsAbleToRepair)}");
        ImGuiEx.Text($"AutoDiscard.IsBusy              = {Show(_dr.AutoDiscardIsBusy)}");
        ImGuiEx.Text($"AutoAetherialReduction.IsBusy   = {Show(_dr.AetherialReductionIsBusy)}");
        ImGuiEx.Text($"FastGrandCompanyExchange.IsBusy = {Show(_dr.GrandCompanyExchangeIsBusy)}");
        ImGuiEx.Text($"AutoSpeedMultiplier.GetMultiplier = {Show(_dr.SpeedMultiplier)}");
        ImGui.Unindent();
    }

    /// <summary>ゲームに影響する IPC。CTRL を押していないと押せない。</summary>
    private void DrawActions()
    {
        ImGuiEx.Checkbox("副作用のあるボタンを有効にする", ref _allowActions,
            enabled: _allowActions || ImGuiEx.Ctrl);
        ImGuiEx.Tooltip("CTRL を押しながらクリック");
        if (!_allowActions) return;

        if (ImGui.Button("LoadModule"))
            _lastResult = $"LoadModule({_moduleName}) = {_dr.LoadModule(_moduleName)}";
        ImGui.SameLine();
        if (ImGui.Button("UnloadModule"))
            _lastResult = $"UnloadModule({_moduleName}) = {_dr.UnloadModule(_moduleName)}";

        if (ImGui.Button("AutoRepair.EnqueueRepair"))
            _lastResult = $"EnqueueRepair = {_dr.EnqueueRepair()}";
        ImGui.SameLine();
        if (ImGui.Button("AetherialReduction.StartReduction"))
            _lastResult = $"StartReduction = {Show(_dr.StartAetherialReduction())}";

        if (ImGui.Button("Speed x1"))
            _lastResult = $"ChangeMultiplier(1) = {_dr.ChangeSpeedMultiplier(1.0f)}";
        ImGui.SameLine();
        if (ImGui.Button("Face: lock north (ground)"))
            _lastResult = $"LockOnGround(north) = {Show(_dr.LockFacingOnGround("north"))}";
        ImGui.SameLine();
        if (ImGui.Button("Face: lock east (bearing 90)"))
            _lastResult = $"LockFacingOnBearing(90) = {_dr.LockFacingOnBearing(90f)}";
        ImGui.SameLine();
        if (ImGui.Button("Face: cancel"))
            _lastResult = $"CancelLockOn = {_dr.CancelFacingLock()}";

        if (ImGui.Button("AntiKnockback.ReplayKnockback"))
            _lastResult = $"ReplayKnockback = {_dr.ReplayKnockback()}";
        ImGui.SameLine();
        if (ImGui.Button("AutoUseEventItem.UseEventItem"))
            _lastResult = $"UseEventItem = {_dr.UseEventItem()}";

        if (_lastResult != "") ImGuiEx.Text(EColor.YellowBright, _lastResult);
    }

    /// <summary>null を "null" と出す。false と区別が付かないと読めないため。</summary>
    private static string Show<T>(T? value) where T : struct => value?.ToString() ?? "null";

    #endregion
}
