using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using ECommons.ImGuiMethods;
using Splatoon.SplatoonScripting;
using System;
using System.Collections.Generic;
using System.Linq;

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
    ///   using ECommons.EzIpcManager;  using System;  using System.Linq;
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

        /// <summary>カメラ方向に自動で向き直すモードの ON/OFF。</summary>
        public bool SetFacingWorkMode(bool enabled) =>
            Send("AutoFaceCameraDirection", () => _faceSetWorkMode(enabled));

        /// <summary>方角で向きを固定する。"North" など。</summary>
        public bool? LockFacingOnGround(string direction) =>
            Gate("AutoFaceCameraDirection") && Mutate() ? _faceLockGround(direction) : null;

        public bool LockFacingOnChara(float rotation) =>
            Send("AutoFaceCameraDirection", () => _faceLockChara(rotation));

        public bool LockFacingOnCamera(float rotation) =>
            Send("AutoFaceCameraDirection", () => _faceLockCamera(rotation));

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
    public override Metadata Metadata => new(1, "Redmoon");

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
        if (ImGui.Button("Face: lock north"))
            _lastResult = $"LockOnGround(North) = {Show(_dr.LockFacingOnGround("North"))}";
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
