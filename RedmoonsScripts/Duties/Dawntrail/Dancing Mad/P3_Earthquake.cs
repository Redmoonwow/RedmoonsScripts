using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.Hooks;
using ECommons.Hooks.ActionEffectTypes;
using ECommons.ImGuiMethods;
using ECommons.Logging;
using FFXIVClientStructs.FFXIV.Client.Game;
using Splatoon;
using Splatoon.Memory;
using Splatoon.SplatoonScripting;
using Splatoon.SplatoonScripting.Priority;

namespace RedmoonsScripts.Duties.Dawntrail.Dancing_Mad;

/// <summary>
/// Dancing Mad (Ultimate) P3 Earthquake / Black Hole guidance.
/// </summary>
/// <remarks>
/// Derived from <c>SplatoonScripts/Duties/Dawntrail/Dancing Mad/P3_Earthquake.cs</c>
/// in PunishXIV/Splatoon at commit <c>d5017695</c>, originally authored by Garume (v41).
/// Licensed under AGPL-3.0, same as the upstream repository.
/// 上流 v41 からの差分は namespace と .NET 規約への追従、および下記 3 点だけ。
/// それ以外の振る舞いは上流のまま。
///   v42 頭上マーカーがラグで遅れたときの誤判定を直す  -> InvalidateMarkerResolution
///   v43 マスターが優先順位リストから 8 人分のマーカーを置く -> TryPlaceMasterMarkers
///   v44 CenterBait の誘導位置を突入時の値で凍結する    -> FreezeFinalInitialAnchorAngle
///   v47 A/B/C/D の起点と 2 本目の距離判定を、線が出そろった瞬間で窓ごとに凍結する
///                                                       -> FreezeWindowDecisions
///   v49 リプレイ中は自分用マーカーを送らず /echo でチャット欄に出す -> QueueMarkerCommand
///   v50 /mk を撃つのが誰かを 1 つのモードにして排他にする (Off / 各自 / マスター)
///                                                       -> Config.MarkerPlacement
///   v48 詠唱通知の 2 経路目 (メモリ監視) を捨て、向きはパケット値だけを使う -> HandleStartingCast
///
/// 上流には region が無く、185 メソッド・呼び出し 12 段のため上から下に読めない。
/// 振る舞いを変えずに region で目次を付け、下に入口からの地図を置いた。
///
/// region の並び (この順に上から):
///   const / static tables / private fields / public properties
///   public methods (SplatoonScript overrides)
///   private methods : settings UI / state transition / slot resolution /
///                     black hole tether / marker command / window advance /
///                     final sequence / reset / display /
///                     geometry, kefka anchor / bucket expectation /
///                     debug text / helpers
///   types
///
/// 入口から最初に呼ばれるもの (ここから辿れば全体に届く):
///   OnSetup             : (何も呼ばない。element 登録のみ)
///   OnDirectorUpdate    : ResetAll
///   OnStartingCast      : HandleStartingCast
///   OnActionEffectEvent : RefreshBasePlayerState, ObserveFinalTowerSource, TryBucket,
///                         AdvanceWindow, HandleFinalAction, Complete
///   OnGainBuffEffect    : RefreshBasePlayerState, StartCollection, GroupFromStatus,
///                         RunMarkerCommand, RunAccretionMarkerCommand,
///                         CancelPendingTargetMarkerCommandForAccretion, ClearSelfResolution
///   OnRemoveBuffEffect  : RefreshBasePlayerState, EnterFinalSequence
///   OnActorControl      : RefreshBasePlayerState, InvalidateMarkerResolution,
///                         TryFinalStackRole, RecordFinalStackRole
///   OnUpdate            : RefreshBasePlayerState, ExecutePendingMarkerCommand,
///                         TryPlaceMasterMarkers, HideElements,
///                         RefreshKefkaAnchorFromObject, ResolveSelfSlot,
///                         PollLiveBlackHoleTethers, ShowGuidance
///
/// 関数の分け方 (上流の 199 本を 129 本に統合した):
///   1 関数は空行込み 100 行までとし、その範囲に収まるなら呼び出しが 1 か所しかない
///   private メソッドは呼び出し元へ畳んだ。イベントハンドラだけは薄いまま残している。
///   戻り値があって return が複数あるものは、消さずにローカル関数として取り込んだ。
///   定数も 2 か所以上で使うものだけを残し、1 か所のものは使用箇所に直接書いた。
///   OnCombatStart / OnCombatEnd / OnReset : ResetAll
///   OnSettingsDraw      : Draw... 系 6 本
///
/// 検証上の注意 (上流からの既知の弱点。振る舞いを変えていないので残っている):
///   自分のスロットしか解決せず、他人の結果を保持する入れ物が無い。
///   したがって 2 人が同じスロットに解決しても検出できない。
///   完全性検査は HasCompleteGroups だけで、しかも既定の PartyMarker モードでは呼ばれない。
///   正しさが 8 人の設定一致に依存し、そのズレを検出する手段が無い。
/// </remarks>
public unsafe class P3_Earthquake : SplatoonScript<P3_Earthquake.Config>
{
    #region const
    /********************************************************************/
    /* const                                                            */
    /********************************************************************/
    // 2 か所以上で使う値だけをここに置いている。1 か所でしか使わない ID や
    // しきい値は、名前の indirection を挟まず使用箇所に直接書いた。
    // ---- territory / object -------------------------------------------------
    private const uint BlackHoleDataId = 19512;             // ブラックホール実体の判定。3 箇所で使用

    // ---- 詠唱 ID (OnStartingCast で拾う。予告 = 表示のきっかけ) ----------------
    private const uint DecisiveBattleChaos = 49890;   // フェーズ移行。ケフカのアンカー角オフセット決定にも使う
    private const uint UltimateEmbrace = 49740;       // 終盤突入の合図 (BowelsOfAgony と対)
    private const uint BowelsOfAgony = 47858;         // 同上
    private const uint LateP3Blizzaga = 47887;        // 終盤: 中央誘導 / ロール散開のきっかけ
    private const uint LandingCast = 47874;           // 着地予告。誘導の actionId としても使う
    private const uint Protrude = 47877;              // 終盤: 散開して動き続ける指示

    // ---- アクション ID (OnActionEffectEvent で拾う。着弾 = 消す / 次へ) --------
    private const uint DondokoHit = 47856;            // 着地。ObserveFinalTowerSource で塔位置を記録
    private const uint TowerImpact = 47857;           // 塔。同上

    // ---- ActorControl ---------------------------------------------------------
    private const uint TargetIconCommand = 34;        // command == 34 が頭上アイコン設定

    // ---- ステータス ID --------------------------------------------------------
    private const ushort AccretionStatus = 1604;  // 追加の割り当て情報。マーカー抑止の判定にも使う
    private const ushort EarthStatus = 5454;      // 地震。付与人数のピークを _earthMaxCount に記録
    private const ushort LineDoneStatus = 5453;   // 線取り済み。自分に付いたら当該ウィンドウ完了

    // ---- 立ち位置の算出 -------------------------------------------------------
    // RefreshExpectedTether の StandPosition: 候補がブラックホールにこれより近ければ、
    // 離れた点を総当たりで探す。半径 3.0 の二乗。
    private const float BlackHoleAvoidRadiusSq = 3.0f * 3.0f;

    // ---- element 名 (OnSetup で登録し、以後この名前で扱う) ---------------------
    private const string DestinationElement = "Destination";
    private const string InstructionElement = "Instruction";
    private const string BlackHoleLineElement = "BlackHoleLine";

    #endregion

    #region static tables
    /********************************************************************/
    /* static tables                                                    */
    /********************************************************************/
    // ウィンドウごとの bucket 表と、設定画面に出す多言語の説明文。
    // どちらも起動時に 1 度作られ、以後変わらない。
    private static readonly Vector3 Center = new(100f, 0f, 100f);
    private static readonly Slot[][] BlackHoleWindowSlots =
    [
        [Slot.Attack1],
        [Slot.Attack1, Slot.Attack2],
        [Slot.Attack1, Slot.Attack2, Slot.Attack3],
        [Slot.Bind1, Slot.Attack2, Slot.Attack3],
        [Slot.Bind1, Slot.Bind2, Slot.Attack3],
        [Slot.Bind1, Slot.Bind2, Slot.Bind3],
        [Slot.Stop1, Slot.Bind2, Slot.Bind3],
        [Slot.Stop1, Slot.Stop2, Slot.Bind3],
        [Slot.Stop1, Slot.Stop2],
        [Slot.Stop2]
    ];
    private static readonly int[] ExpectedSourcesByWindow = BlackHoleWindowSlots.Select(x => x.Length).ToArray();
    private static readonly int[] SelectableMarkerIds = [0, 1, 2, 5, 6, 7, 8, 9];
    private static readonly string[] SelectableMarkerNames = ["Attack1", "Attack2", "Attack3", "Bind1", "Bind2", "Bind3", "Stop1", "Stop2"];
    private static readonly string[] BlackHoleOrderNames = ["1st", "2nd", "3rd"];
    private static readonly string[] FinalInitialBaitModeNames = ["Center", "Kefka-relative N/S"];
    private static readonly string[] FinalNorthRoleNames = ["Support", "DPS"];
    private static readonly string[] MarkerCommandSourceNames = ["Target debuff", "Accretion debuff"];
    private static readonly string[] MarkerPlacementNames =
        ["Off", "Each player marks themselves", "Master marks everyone"];
    private static readonly AssignmentMode[] AssignmentModeValues =
    [
        AssignmentMode.PartyMarker,
        AssignmentMode.Priority,
        AssignmentMode.RoleAccretion,
        AssignmentMode.FixedRoleAccretion,
        AssignmentMode.FixedMarkerLanes
    ];
    private static readonly string[] AssignmentModeNames =
        ["Party marker", "Priority", "PF role/accretion", "Fixed role/accretion spots", "Fixed marker lanes"];
    private static readonly string[] MapMarkerNames = ["A", "B", "C", "D"];
    private static readonly string[] LineBaitDirectionNames = ["Clockwise", "Counterclockwise"];
    private static readonly string[] FirstWindowBaitDirectionNames = ["Same as line bait direction", "Clockwise", "Counterclockwise"];
    private static readonly string[] FirstPairAssignmentNames = ["Source order", "First slot nearest"];
    private static readonly string[] FirstOrbRoleNames = ["DPS", "Support"];
    private static readonly RolePosition[] DefaultRolePriority =
    [
        RolePosition.T1,
        RolePosition.T2,
        RolePosition.H1,
        RolePosition.H2,
        RolePosition.M1,
        RolePosition.M2,
        RolePosition.R1,
        RolePosition.R2
    ];

    // 表示文言。2 か所以上で使うものだけを置いている。
    // 1 か所でしか出さない説明文は InternationalString.Print で使用箇所に直接書いた。
    private static readonly InternationalString Description = new()
    {
        En = "P3 Earthquake helper. It resolves your First/Second/Third line order from the debuff plus party markers or priority, then follows live Black Hole tether changes. When the line is on you, it shows the Black Hole-to-player line and your bait position. The tether uses the configured correct/wrong/unknown colors depending on whether the current active Black Hole order matches your slot.",
        Jp = "P3地震用です。デバフとマーカーまたは優先順位から自分の第一/第二/第三対象内の線取り順を決め、ブラックホールテザーの付け替わりを追ってナビします。自分に線が付いた時は、ブラックホールから自分への線と誘導先を表示します。現在線が出ているブラックホールの並びと自分のスロットが一致するかどうかで、設定した正解/不一致/不明の色を使います。"
    };

    #endregion

    #region private fields
    /********************************************************************/
    /* private fields                                                   */
    /********************************************************************/
    // 実行時に変化する状態。すべてこの区画にあり、他所では宣言されない。

    // 状態はどの Clear が戻すかで塊になっている。ResetAll から下記の順に呼ばれる:
    //   ResetAll -> ClearMechanicState -> ClearSelfResolution / ClearSelfDisplayState
    //                                  -> ClearGuide
    //                                  -> ClearBlackHoleState
    // フィールドを足したら、対応する Clear にも足すこと (型では守られていない)。

    // ---- ResetAll が直接戻す: 戦闘・ディレクタ単位で完全に初期化される ---------
    private State _state;                       // 進行の主状態。Idle/Collecting/BlackHoleActive/Final/Completed
    private uint _selfPlayerId;                 // 自機の EntityId。BasePlayer 差し替え検出に使う
    private readonly Dictionary<uint, TargetGroup> _groups = [];  // EntityId -> 第1/第2/第3対象。デバフから構築
    private bool _sentMarkerCommand;            // マーカー送信の一発ガード。二重送信を防ぐ
    private string _pendingMarkerCommand = "";  // 送信待ちのコマンド文字列
    private long _markerCommandAtMs;            // 送信予定時刻 (TickCount64)。0 なら予定なし
    private bool _pendingTargetMarkerCommand;   // 送信待ちが「対象デバフ由来」かどうか
    // 直前にパケット経路で見た詠唱。メモリ監視経路の同じ詠唱を捨てるための照合用
    private uint _lastPacketCastSource;         // 詠唱者の EntityId
    private uint _lastPacketCastId;             // アクション ID
    private long _lastPacketCastAtMs;           // 見た時刻 (TickCount64)

    // ---- ClearMechanicState が戻す: ギミック 1 回分 ----------------------------
    private readonly HashSet<uint> _earthPlayers = [];      // 地震デバフ保持者
    private readonly HashSet<uint> _accretionPlayers = [];  // Accretion 保持者
    private uint _kefkaId;                       // 確定したケフカの EntityId。0 = 未確定
    private Vector3? _kefkaPosition;             // 確定したケフカ位置。角度推定で埋めることもある
    private string _kefkaAnchorDebug = "";       // どう確定したかの記録 (Debug 表示用)
    private int _earthMaxCount;                  // 地震デバフ保持者数のピーク
    private int _currentWindow = -1;             // 現在のブラックホールウィンドウ 0..9。-1 = 未開始
    private bool _selfHadAccretionMarkerBlock;   // Accretion によりマーカー送信を抑止したか
    private bool _placedMasterMarkers;           // マスターが 8 人分のマーカーを置いたか。一発ガード

    // ---- ClearMechanicState が終盤ぶんとして戻す -------------------------------
    private FinalStage _finalStage;                    // 終盤の細分状態
    // CenterBait 突入時に凍結する誘導の材料。null / Unknown のあいだは未凍結。
    // なぜ凍結するかは FreezeFinalInitialAnchorAngle を参照。
    private float? _finalInitialAnchorAngle;           // 凍結したケフカの向き (rad)
    private FinalStackRole _finalInitialStackRole;     // 凍結した自分の頭割りロール
    private uint _finalInitialRoleOwner;               // 上の値が誰のものか。BasePlayer 差し替え検出用
    private FinalStackRole _firstFinalStackRole;       // 1 回目の頭割りロール
    private FinalStackRole _secondFinalStackRole;      // 2 回目の頭割りロール
    private FinalStackRole _currentFinalStackRole;     // 現在参照しているロール
    private int _landingCount;                         // 着地の回数
    private int _finalStackMarkerCount;                // 頭割りマーカーの観測回数
    private int _finalDondokoHitCount;                 // 着地アクションの観測回数
    private readonly List<Vector3> _finalTowerPositions = [];  // 観測した塔の位置

    // ---- ClearSelfResolution / ClearSelfDisplayState が戻す: 自分の解決結果 ----
    private Slot _selfSlot;                      // 自分のスロット。None = 未解決
    private AssignmentQuality _quality;          // どの根拠で解決したか (Marker/Priority/RoleAccretion)
    private BlackHoleTask? _selfBlackHoleTask;   // 自分が取る線。bucket / 始点 / 相手 / 立ち位置
    private int _selfCompletedWindow = -1;       // 完了済みウィンドウ。_currentWindow と一致で完了
    private string _instruction = "";            // 画面に出す指示文

    // ---- ClearGuide が戻す: 終盤の誘導表示 ------------------------------------
    private Vector3? _guideDestination;      // 誘導先。null なら非表示
    private GuidanceKind _guideKind;         // 誘導の種類
    private uint _guideActionId;             // どのアクションに紐づく誘導か
    private string _guideText = "";          // 誘導の本文
    private string _guideInstruction = "";   // 併記する指示
    private string _guideDebug = "";         // 算出根拠 (Debug 表示用)

    // ---- ClearBlackHoleState が戻す: ブラックホールの実体 ----------------------
    private readonly List<Vector3> _blackHolePositions = [];  // 生存中のブラックホール座標
    private readonly HashSet<int> _hitSources = [];           // 着弾済みの bucket

    // ---- ClearCurrentWindowTethers が戻す: 現ウィンドウの線 --------------------
    private readonly Dictionary<int, uint> _tetherTargets = [];     // bucket -> 線の相手
    private readonly Dictionary<int, Vector3> _tetherSources = [];  // bucket -> 線の始点
    // 線が出そろった瞬間に確定し、窓のあいだ動かさない値。null = 未確定。FreezeWindowDecisions 参照
    private float? _windowOrderAnchorAngle;                         // A/B/C/D の起点 (巨大ケフカの方角)
    private (int First, int Second)? _windowFirstPair;              // 2 本目の窓: 攻撃 1 / 攻撃 2 の bucket

    // ---- ClearFixedLaneSetBuckets が戻す: FixedMarkerLanes モード専用 ----------
    private readonly int[] _fixedLaneSetBuckets = [-1, -1, -1];  // レーンごとに確定した bucket
    private int _fixedLaneSetStartWindow = -1;                   // そのキャッシュが有効な開始ウィンドウ

    /// <summary>Position of the Black Hole this player is assigned to, i.e. the tether origin.</summary>
    private Vector3? SelfTetherSource => _selfBlackHoleTask?.Source;

    /// <summary>Entity id tethered to this player's assigned Black Hole, or 0 when unassigned.</summary>
    private uint SelfTetherTarget => _selfBlackHoleTask?.Target ?? 0;

    /// <summary>Lane bucket of this player's assigned Black Hole, or -1 when unassigned.</summary>
    private int SelfTetherBucket => _selfBlackHoleTask?.Bucket ?? -1;

    #endregion

    #region public properties
    /********************************************************************/
    /* public properties                                                */
    /********************************************************************/
    // スクリプトの識別情報。ValidTerritories と Metadata のみ。
    public override HashSet<uint>? ValidTerritories { get; } = [1363];   // Dancing Mad (Ultimate)
    public override Metadata Metadata => new(50, "Garume, Redmoon");

    #endregion

    #region public methods (SplatoonScript overrides)
    /********************************************************************/
    /* public methods (SplatoonScript overrides)                        */
    /********************************************************************/
    // Splatoon から呼ばれる入口。
    // 詠唱 (OnStartingCast) = 予告して表示、着弾 (OnActionEffectEvent) = 消して次へ、
    // という役割分担になっている。判定の実体は下の private 区画にある。
    public override void OnSetup()
    {
        C.EnsureDefaults();
        Controller.RegisterElement(DestinationElement, new Element(0)
        {
            Enabled = false,
            radius = 1.25f,
            thicc = 15.0f,
            fillIntensity = 0.25f,
            color = C.RainbowNavigationColor1,
            tether = true,
            overlayBGColor = 0xC8000000,
            overlayTextColor = 0xFFFFFFFF,
            overlayVOffset = 2.4f,
            overlayFScale = 1.5f
        });
        Controller.RegisterElement(InstructionElement, new Element(0)
        {
            Enabled = false,
            radius = 0,
            thicc = 0,
            overlayBGColor = 0xC8000000,
            overlayTextColor = 0xFFFFFFFF,
            overlayVOffset = 3.0f,
            overlayFScale = 1.7f
        });
        Controller.RegisterElement(BlackHoleLineElement, new Element(2)
        {
            Enabled = false,
            radius = 0,
            thicc = 15.0f,
            color = C.WrongTetherColor
        });
    }

    public override void OnCombatStart() => ResetAll();
    public override void OnCombatEnd() => ResetAll();
    public override void OnReset() => ResetAll();

    public override void OnDirectorUpdate(DirectorUpdateCategory category)
    {
        if (category is DirectorUpdateCategory.Commence or DirectorUpdateCategory.Recommence or DirectorUpdateCategory.Wipe)
            ResetAll();
    }

    public override void OnStartingCast(uint source, uint castId)
    {
        HandleStartingCast(source, castId, "attached-info", null);
    }

    public override unsafe void OnStartingCast(uint sourceId, PacketActorCast* packet)
    {
        HandleStartingCast(sourceId, packet->ActionID, "packet", packet);
    }

    private unsafe void HandleStartingCast(uint source, uint castId, string origin, PacketActorCast* packet)
    {
        RefreshBasePlayerState();
        // 1 つの詠唱は 2 経路から届く。先にパケット (向きはサーバ値、全クライアント同一)、
        // 次にメモリ監視 (向きはこのクライアントの補間値)。後者は前者を上書きしてしまうので、
        // 直前にパケットで同じ詠唱を見ていればここで捨て、パケットが来なかったときだけ通す。
        if (packet != null)
        {
            _lastPacketCastSource = source;
            _lastPacketCastId = castId;
            _lastPacketCastAtMs = Environment.TickCount64;
        }
        else if (source == _lastPacketCastSource && castId == _lastPacketCastId &&
                 Environment.TickCount64 - _lastPacketCastAtMs < 500)
        {
            return;
        }

        var packetRotation = packet == null ? (float?)null : packet->Rotation;
        UpdateKefkaAnchor(source, castId, packetRotation);
        ObserveFinalTowerSource(source.GetObject(), null, castId, origin);

        if (castId is UltimateEmbrace or BowelsOfAgony)
            ResetAll();
        else if (castId is DecisiveBattleChaos or 49891)   // 49891 = エクスデス側のパターン
            StartCollection();
        else if (castId == 47867)   // 47867 = ブラックホール出現
        {
            StartCollection();
            _state = State.BlackHoleActive;
            _currentWindow = 0;
            _hitSources.Clear();
            CacheLiveBlackHoleActors();
            ClearCurrentWindowTethers();
            ClearFixedLaneSetBuckets();
            ClearSelfTether(true);
            ClearGuide();
        }
        else if (castId == LateP3Blizzaga)
        {
            // 終盤 1 手目: 中央誘導。ロールで N/S に割るモードならここで位置が決まる。
            EnterFinalSequence();
            _finalStage = FinalStage.CenterBait;
            // ロールが取れるかどうかに関わらず、CenterBait に入ったこの瞬間のケフカの向きを確定させる。
            // 後からロールが解決したとき、そのときのケフカ位置ではなく突入時の向きを使わせるため。
            FreezeFinalInitialAnchorAngle();
            if (TryGetFinalInitialBaitGuide(out var destination, out var text))
                SetGuide(destination, text, GuidanceKind.FinalCenter, LateP3Blizzaga, 0.0f, 0.0f);
            else
                SetInstruction(TextOrEmpty(C.ShowFinalCenterText, C.FinalCenterText), GuidanceKind.FinalCenter);
        }
        else if (castId == Protrude)
        {
            // 終盤最後: 散開して動き続ける。
            EnterFinalSequence();
            _finalStage = FinalStage.ProtrudeMove;
            SetFinalRoleGuide(C.ShowFinalMoveText, C.FinalMoveText, GuidanceKind.FinalMove, Protrude);
        }
        // 47855 / LandingCast は着地予兆。予告では何もせず、着弾側で処理する。
    }

    public override void OnActionEffectEvent(ActionEffectSet set)
    {
        RefreshBasePlayerState();

        var actionId = set.Action?.RowId ?? 0;
        ObserveFinalTowerSource(set.Source, set.Position, actionId, "action");
        if (actionId == 47868 && TryBucket(set.Source?.Position ?? set.Position, out var bucket))   // 47868 = ブラックホール着弾
        {
            AdvanceWindow(bucket);
        }
        else if (!HandleFinalAction(set, actionId) &&
            actionId is UltimateEmbrace or BowelsOfAgony)
        {
            Complete();
        }
    }

    private bool HandleFinalAction(ActionEffectSet set, uint actionId)
    {
        if (actionId == LateP3Blizzaga)
        {
            EnterFinalSequence();
            _finalStage = FinalStage.RoleSpread;
            SetFinalRoleGuide(C.ShowFinalSpreadText, C.FinalSpreadText, GuidanceKind.FinalSpread, LateP3Blizzaga);
            return true;
        }
        if (actionId == 47885)   // 47885 = 2 回目の着地へ切り替え
        {
            // 1 回目のロールが観測できているときだけ動く。未観測なら次の着弾を待つ。
            if (_firstFinalStackRole != FinalStackRole.Unknown)
                SetFinalLanding(_firstFinalStackRole);
            return true;
        }
        if (actionId == DondokoHit)
        {
            _finalDondokoHitCount++;
            if (_finalDondokoHitCount == 2)
            {
                // 2 回目のロールが観測できていればそれを使い、無ければ 1 回目の裏を取る。
                var stackRole = _secondFinalStackRole != FinalStackRole.Unknown
                    ? _secondFinalStackRole
                    : _firstFinalStackRole switch
                    {
                        FinalStackRole.Support => FinalStackRole.Dps,
                        FinalStackRole.Dps => FinalStackRole.Support,
                        _ => FinalStackRole.Unknown
                    };
                _landingCount = 1;
                SetFinalLanding(stackRole);
            }
            return true;
        }
        if (actionId == TowerImpact)
            return true;
        if (actionId is Protrude)
        {
            Complete();
            return true;
        }

        return false;
    }

    public override void OnActorControl(uint sourceId, uint command, uint p1, uint p2, uint p3, uint p4, uint p5,
        uint p6, uint p7, uint p8, ulong targetId, byte replaying)
    {
        RefreshBasePlayerState();

        // 頭上マーカーが動いたこと自体が「答えが変わったかもしれない」合図。
        // マーカー由来のスロット解決だけを捨てる。詳細は InvalidateMarkerResolution。
        if (command == TargetIconCommand)
            InvalidateMarkerResolution();

        if (command == TargetIconCommand && p1 == 161 &&   // 161 = 終盤の頭割りマーカー
            _state is not (State.Idle or State.Completed) &&
            TryFinalStackRole(sourceId, out var role))
            RecordFinalStackRole(role);
    }

    public override void OnGainBuffEffect(uint sourceId, Status status)
    {
        RefreshBasePlayerState();
        var isSelf = sourceId == BasePlayer?.EntityId;

        if (status.StatusId == EarthStatus)
        {
            StartCollection();
            _earthPlayers.Add(sourceId);
            _earthMaxCount = Math.Max(_earthMaxCount, _earthPlayers.Count);
            return;
        }
        if (status.StatusId == AccretionStatus)
        {
            StartCollection();
            _accretionPlayers.Add(sourceId);
            if (isSelf)
            {
                _selfHadAccretionMarkerBlock = true;
                CancelPendingTargetMarkerCommandForAccretion();
                ClearSelfResolution();
                RunAccretionMarkerCommand();
            }
            return;
        }
        if (isSelf && status.StatusId == LineDoneStatus)
        {
            _selfHadAccretionMarkerBlock = true;
            CancelPendingTargetMarkerCommandForAccretion();
            return;
        }

        if (GroupFromStatus(status.StatusId) is not { } group) return;
        StartCollection();
        _groups[sourceId] = group;

        if (isSelf)
        {
            ClearSelfResolution();
            RunMarkerCommand(group);
        }
    }

    public override void OnRemoveBuffEffect(uint sourceId, Status status)
    {
        RefreshBasePlayerState();

        if (status.StatusId == EarthStatus)
        {
            var hadEarth = _earthPlayers.Remove(sourceId);
            if (hadEarth && _state == State.BlackHoleActive && _earthMaxCount >= 8 && _earthPlayers.Count == 0)
                EnterFinalSequence();
            return;
        }
        if (status.StatusId == AccretionStatus ||
            sourceId == BasePlayer?.EntityId && status.StatusId == LineDoneStatus)
            return;
    }

    public override void OnUpdate()
    {
        RefreshBasePlayerState();
        ExecutePendingMarkerCommand();
        TryPlaceMasterMarkers();

        HideElements();
        RefreshKefkaAnchorFromObject();
        ResolveSelfSlot();
        if (_state == State.BlackHoleActive)
            PollLiveBlackHoleTethers();
        if (BasePlayer == null || _state is State.Idle or State.Completed) return;

        ShowGuidance();
    }

    /// <summary>毎フレームの表示更新。線 -> ブラックホールの立ち位置 -> 終盤の誘導、の順に出す。
    /// 設定で終盤ナビや線のみ表示に絞られている場合はここで打ち切る。</summary>
    private void ShowGuidance()
    {
        if (_state == State.FinalSequence && !C.ShowPostBlackHoleNavigation)
            return;

        // 線: 自分の担当ブラックホールから相手へ。色は期待と実際が合っているかを表す。
        if (SelfTetherSource is { } source &&
            SelfTetherTarget.GetObject() is { } lineTarget &&
            Controller.TryGetElementByName(BlackHoleLineElement, out var lineElement))
        {
            var expected = ExpectedBucket(_selfSlot);
            lineElement.Enabled = true;
            lineElement.color = expected < 0 || SelfTetherBucket < 0
                ? C.UnknownTetherColor
                : expected != SelfTetherBucket || !IsSelfTetherTarget()
                    ? C.WrongTetherColor
                    : C.CorrectTetherColor;
            lineElement.SetRefPosition(source);
            lineElement.SetOffPosition(lineTarget.Position);
        }

        if (C.BlackHoleTetherOnly && _state == State.BlackHoleActive)
            return;

        // 立ち位置: 自分が線を取る側なら算出した点へ、取られる側なら相手の足元へ。
        // ブラックホールの立ち位置が出せたときは、終盤の誘導より優先する。
        var showedBlackHoleDestination = false;
        if (_selfBlackHoleTask is { } task)
        {
            if (IsSelfTetherTarget())
            {
                ShowDestination(task.StandPosition, "");
                showedBlackHoleDestination = true;
            }
            else if (SelfTetherTarget.GetObject() is { } standTarget)
            {
                ShowDestination(FlatPosition(standTarget.Position), "");
                showedBlackHoleDestination = true;
            }
        }

        if (!showedBlackHoleDestination && _guideDestination is { } guide)
            ShowDestination(guide, _guideText);

        if (!string.IsNullOrWhiteSpace(_guideInstruction))
            ShowInstruction(_guideInstruction);
        else if (!string.IsNullOrWhiteSpace(_instruction))
            ShowInstruction(_instruction);
    }

    public override void OnSettingsDraw()
    {
        C.EnsureDefaults();
        ImGui.TextWrapped(Description.Get());
        ImGui.Separator();

        DrawAssignmentSettings();
        DrawMarkerCommandSettings();
        DrawFinalRolePositionSettings();
        DrawVisualSettings();
        DrawDisplayTextSettings();
        DrawDebugStatus();
    }

    #endregion

    #region private methods : settings UI
    /********************************************************************/
    /* private methods : settings UI                                    */
    /********************************************************************/
    // OnSettingsDraw から呼ばれる描画のみ。ロジックを持たない。
    // 設定は 62 項目あり、割り当てモード 5 種それぞれに対応する UI がここに並ぶ。
    private void DrawAssignmentSettings()
    {
        ImGui.TextUnformatted("Assignment");
        ImGui.Indent();
        var modeIndex = Array.IndexOf(AssignmentModeValues, C.AssignmentMode);
        var mode = modeIndex < 0 ? 0 : modeIndex;
        if (DrawCombo("Assignment mode", ref mode, AssignmentModeNames, 260f))
            C.AssignmentMode = AssignmentModeValues[Math.Clamp(mode, 0, AssignmentModeValues.Length - 1)];
        ImGui.TextWrapped(InternationalString.Print(
            en: "Party marker: uses only the marker line-order table below. Priority: ignores markers and orders players with the priority list inside each First/Second/Third group. PF role/accretion: resolves order inside your group from First orb role plus Accretion. Fixed role/accretion spots: support=A, DPS=B, Accretion=C; if that preferred spot has no active Black Hole, it uses D. Fixed marker lanes: resolves your lane from role/accretion, then searches from configured markers and directions.",
            jp: "Party marker: 下のマーカー別線取り順だけで判定します。Priority: マーカーを無視し、第一/第二/第三対象ごとに優先順位で並べます。PF role/accretion: First orb role と Accretion から自分のグループ内の順番を判定します。Fixed role/accretion spots: タンク/ヒラ=A、DPS=B、Accretion=C として扱い、担当spotにブラックホールが無い場合はDを使います。Fixed marker lanes: ロール/Accretionから担当レーンを決め、設定したマーカーと方向から取る線を探します。"));

        if (C.AssignmentMode is AssignmentMode.PartyMarker)
        {
            DrawSubsection("Party marker assignment");
            ImGui.Indent();
            ImGui.TextUnformatted("Black Hole line order for each marker:");
            ImGui.TextWrapped(InternationalString.Print(
                en: "Set which line order each party marker means. The debuff decides the group: First Target, Second Target, or Third Target. The marker decides the order inside that group. Example: Attack1 = 1st means First Target + Attack1 becomes First1, while Second Target + Attack1 becomes Second1. Third Target has only two players, so do not assign 3rd to markers used by Third Target players.",
                jp: "各マーカーが何番目の線取りを意味するかを設定します。第一/第二/第三対象のどのグループかはデバフで決まり、グループ内の何番目かをマーカーで決めます。例: Attack1 = 1st の場合、第一対象+Attack1 は First1、第二対象+Attack1 は Second1 になります。第三対象は2人だけなので、第三対象に使うマーカーへ 3rd は割り当てないでください。"));
            for (var i = 0; i < SelectableMarkerIds.Length; i++)
            {
                var selected = Math.Clamp(C.MarkerLineOrders[i], 0, BlackHoleOrderNames.Length - 1);
                ImGui.SetNextItemWidth(160f);
                if (ImGui.Combo($"{SelectableMarkerNames[i]} line order", ref selected,
                        BlackHoleOrderNames, BlackHoleOrderNames.Length))
                    C.MarkerLineOrders[i] = selected;
            }
            ImGui.Unindent();
        }

        if (C.AssignmentMode is AssignmentMode.RoleAccretion or AssignmentMode.FixedMarkerLanes)
        {
            var firstOrbRole = (int)C.FirstOrbRole;
            if (DrawCombo("First orb role", ref firstOrbRole, FirstOrbRoleNames, 180f))
                C.FirstOrbRole = (FirstOrbRole)Math.Clamp(firstOrbRole, 0, FirstOrbRoleNames.Length - 1);
        }

        if (C.AssignmentMode is AssignmentMode.FixedRoleAccretion)
        {
            ImGui.TextUnformatted("DPS/Support/Accretion spot assignment");
            ImGui.Indent();
            DrawMapMarkerCombo("DPS", ref C.DpsMarker);
            DrawMapMarkerCombo("Support", ref C.SupportMarker);
            DrawMapMarkerCombo("Accretion", ref C.AccretionMarker);
            DrawMapMarkerCombo("Fallback", ref C.FallbackMarker);

            if (C.DpsMarker == C.SupportMarker || C.DpsMarker == C.AccretionMarker || C.DpsMarker == C.FallbackMarker ||
                C.SupportMarker == C.AccretionMarker || C.SupportMarker == C.FallbackMarker ||
                C.AccretionMarker == C.FallbackMarker)
                ImGui.TextWrapped("Warning: DPS, Support, Accretion, and Fallback markers should be unique.");

            ImGui.Unindent();
        }
        else if (C.AssignmentMode is AssignmentMode.FixedMarkerLanes)
        {
            ImGui.TextUnformatted("Fixed marker lanes");
            ImGui.Indent();
            if (ImGui.Button("Apply DPS=A / Support=D / Accretion=B / Flex=C"))
            {
                C.FirstOrbRole = FirstOrbRole.Dps;
                C.LaneDpsMarker = MapMarker.A;
                C.LaneSupportMarker = MapMarker.D;
                C.LaneAccretionMarker = MapMarker.B;
                C.LaneFlexMarker = MapMarker.C;
                C.DpsLineBaitDirection = LineBaitDirection.Clockwise;
                C.SupportLineBaitDirection = LineBaitDirection.Counterclockwise;
            }
            DrawMapMarkerCombo("DPS marker", ref C.LaneDpsMarker);
            DrawLineBaitDirectionCombo("DPS direction", ref C.DpsLineBaitDirection);
            DrawMapMarkerCombo("Support marker", ref C.LaneSupportMarker);
            DrawLineBaitDirectionCombo("Support direction", ref C.SupportLineBaitDirection);
            DrawMapMarkerCombo("Accretion marker", ref C.LaneAccretionMarker);
            DrawLineBaitDirectionCombo("Accretion direction", ref C.AccretionLineBaitDirection);
            DrawMapMarkerCombo("Flex marker", ref C.LaneFlexMarker);
            if (C.LaneDpsMarker == C.LaneSupportMarker || C.LaneDpsMarker == C.LaneAccretionMarker || C.LaneDpsMarker == C.LaneFlexMarker ||
                C.LaneSupportMarker == C.LaneAccretionMarker || C.LaneSupportMarker == C.LaneFlexMarker ||
                C.LaneAccretionMarker == C.LaneFlexMarker)
                ImGui.TextWrapped("Warning: DPS, Support, Accretion, and Flex markers should be unique.");
            ImGui.Unindent();
        }

        if (C.AssignmentMode is AssignmentMode.Priority)
        {
            if (ImGui.Button("Apply Meow static TN priority"))
                C.PriorityData = CreatePriorityData("P3 Earthquake Meow static TN priority",
                    "M1 - M2 - R1 - R2 - MT - OT - H1 - H2.",
                    [
                        RolePosition.M1, RolePosition.M2, RolePosition.R1, RolePosition.R2,
                        RolePosition.T1, RolePosition.T2, RolePosition.H1, RolePosition.H2
                    ]);
            C.PriorityData.Draw();
        }

        DrawBlackHoleSettings();
        ImGui.Unindent();
    }

    private void DrawFinalRolePositionSettings()
    {
        if (!ImGui.CollapsingHeader("Final role positions")) return;

        ImGui.TextWrapped(InternationalString.Print(
            en: "The late fixed spread/tower guide uses these role positions even when Earthquake line assignment mode is Party marker.",
            jp: "終盤の固定散開/塔ナビは、地震線取りの Assignment mode が Party marker の場合でもこのロール設定を使用します。"));
        if (C.AssignmentMode is not AssignmentMode.Priority)
            C.PriorityData.Draw();

        ImGui.Spacing();
        var mode = (int)C.FinalInitialBaitMode;
        if (DrawCombo("Initial bait position", ref mode, FinalInitialBaitModeNames, 220f))
            C.FinalInitialBaitMode = (FinalInitialBaitMode)Math.Clamp(mode, 0, FinalInitialBaitModeNames.Length - 1);
        if (C.FinalInitialBaitMode == FinalInitialBaitMode.KefkaRelativeRoleSplit)
        {
            var northRole = (int)C.FinalInitialNorthRole;
            if (DrawCombo("Kefka-relative north role", ref northRole, FinalNorthRoleNames, 180f))
                C.FinalInitialNorthRole = (FinalInitialNorthRole)Math.Clamp(northRole, 0, FinalNorthRoleNames.Length - 1);
        }
        ImGui.TextWrapped(InternationalString.Print(
            en: "Center keeps the existing all-center bait. Kefka-relative N/S splits the first bait by combat role using the frozen Kefka-foot direction: the selected north role goes Kefka-relative north, the other role goes south.",
            jp: "Center は既存の全員中央誘導です。Kefka-relative N/S は、固定したケフカ足元方向を基準に、選択したロールをケフカ基準北、もう片方を南へ誘導します。"));
    }

    private void DrawMapMarkerCombo(string label, ref MapMarker marker)
    {
        var value = (int)marker;
        if (DrawCombo(label, ref value, MapMarkerNames, 180f))
            marker = (MapMarker)Math.Clamp(value, 0, MapMarkerNames.Length - 1);
    }

    private void DrawLineBaitDirectionCombo(string label, ref LineBaitDirection direction)
    {
        var value = (int)direction;
        if (DrawCombo(label, ref value, LineBaitDirectionNames, 180f))
            direction = (LineBaitDirection)Math.Clamp(value, 0, LineBaitDirectionNames.Length - 1);
    }

    private void DrawBlackHoleSettings()
    {
        DrawSubsection("Black Hole");
        ImGui.Indent();
        var direction = (int)C.LineBaitDirection;
        if (DrawCombo("Line bait direction", ref direction, LineBaitDirectionNames, 180f))
            C.LineBaitDirection = (LineBaitDirection)Math.Clamp(direction, 0, 1);
        ImGui.TextWrapped(InternationalString.Print(
            en: "Line bait direction controls where your bait marker is placed from the Black Hole that is currently tethered to you. Clockwise and Counterclockwise are relative to the arena center. In Fixed marker lanes, DPS/Support directions choose the source search order; this setting still controls the final bait offset except when the first-window override is used.",
            jp: "Line bait direction は、自分に付いたブラックホールを基準に誘導先を時計回り/反時計回りのどちらへずらすかを決めます。方向はフィールド中央基準です。Fixed marker lanes では DPS/Support direction は線の探索順にだけ使います。First window bait direction で別の方向を選んでいる場合を除き、実際に線を引っ張る位置はこの設定で決まります。"));
        var firstWindowDirection = (int)C.FirstWindowBaitDirection;
        if (DrawCombo("First window bait direction", ref firstWindowDirection, FirstWindowBaitDirectionNames, 260f))
            C.FirstWindowBaitDirection = (FirstWindowBaitDirection)Math.Clamp(firstWindowDirection, 0, FirstWindowBaitDirectionNames.Length - 1);
        ImGui.TextWrapped(InternationalString.Print(
            en: "First window bait direction overrides only the bait position for the first Black Hole window. It does not change which Black Hole is selected.",
            jp: "最初のブラックホール window だけ、線を引っ張る誘導先方向を上書きします。取るブラックホール自体は変えません。"));
        var firstPairAssignment = (int)C.FirstPairAssignment;
        if (DrawCombo("First pair assignment", ref firstPairAssignment, FirstPairAssignmentNames, 220f))
            C.FirstPairAssignment = (FirstPairAssignment)Math.Clamp(firstPairAssignment, 0, FirstPairAssignmentNames.Length - 1);
        ImGui.TextWrapped(InternationalString.Print(
            en: "First pair assignment controls how the first two-line Black Hole window selects sources. Source order uses the configured Black Hole source order. First slot nearest makes the first slot take the visible Black Hole closest to that player, and the second slot takes the other visible Black Hole.",
            jp: "First pair assignment は、最初に2本出るブラックホール window の線選択を決めます。Source order は設定した Black Hole source order を使います。First slot nearest は、1番目のスロットがそのプレイヤーに最も近いブラックホールを取り、2番目のスロットがもう片方を取ります。"));

        var sourceOrder = (int)C.BlackHoleSourceOrder;
        if (DrawCombo("Black Hole source order", ref sourceOrder, ["Clockwise", "Counterclockwise"], 180f))
            C.BlackHoleSourceOrder = (BlackHoleSourceOrder)Math.Clamp(sourceOrder, 0, 1);
        var anchor = (int)C.BlackHoleOrderAnchor;
        if (DrawCombo("Black Hole order anchor", ref anchor, ["Kefka position", "Arena north"], 260f))
            C.BlackHoleOrderAnchor = (BlackHoleOrderAnchor)Math.Clamp(anchor, 0, 1);
        ImGui.TextWrapped(InternationalString.Print(
            en: "Black Hole source order sorts only the Black Holes that currently have active tethers. The anchor decides where 1st starts; the order decides clockwise or counterclockwise from that anchor.",
            jp: "Black Hole source order は、現在線が出ているブラックホールだけを並べ替える設定です。anchor で 1番目を数え始める基準を決め、order でそこから時計回り/反時計回りのどちらに数えるかを決めます。"));
        ImGui.Checkbox("Black Hole tether only", ref C.BlackHoleTetherOnly);
        ImGui.TextWrapped(InternationalString.Print(
            en: "When enabled, Black Hole windows show only the Black Hole tether line. Destination circles and waiting text are hidden during Black Hole.",
            jp: "有効にすると、ブラックホール中はブラックホールのテザー線だけを表示します。誘導先の円と待機テキストは非表示になります。"));
        ImGui.Checkbox("Show post-Black-Hole final navigation", ref C.ShowPostBlackHoleNavigation);
        ImGui.TextWrapped(InternationalString.Print(
            en: "Show or hide the final-sequence navigation after the Black Hole windows. Black Hole tether tracking and assignments still run even when this is disabled.",
            jp: "ブラックホール後の最終ギミック用ナビを表示するかを切り替えます。OFFでもブラックホールの線追跡と割り当て処理は動作します。"));
        ImGui.Unindent();
    }

    private void DrawMarkerCommandSettings()
    {
        if (!ImGui.CollapsingHeader("Marker command")) return;

        ImGui.Indent();
        ImGui.TextWrapped(InternationalString.Print(
            en: "Optional self-marker commands. Target debuff source uses the First/Second/Third group, not the line order. It can also skip or cancel the target marker when you have Accretion or Faded Accretion. Accretion debuff source queues the Accretion command when you receive Accretion. The script waits a random delay between min and max seconds, then executes the queued command. Commands are not executed during replay playback.",
            jp: "任意の自分用マーカーコマンドです。Target debuff を選ぶと線取り順ではなく第一/第二/第三対象のデバフグループで実行します。AccretionまたはFaded Accretion持ちの時だけ、target markerをスキップ/キャンセルする設定も使えます。Accretion debuff を選ぶと自分にAccretionが付いた時にAccretion commandを予約します。minからmax秒のランダムディレイ後に実行し、リプレイ再生中は実行しません。"));
        ImGui.Spacing();
        var placement = (int)C.MarkerPlacement;
        if (DrawCombo("Marker placement", ref placement, MarkerPlacementNames, 280f))
            C.MarkerPlacement = (MarkerPlacement)Math.Clamp(placement, 0, MarkerPlacementNames.Length - 1);
        ImGui.TextWrapped(InternationalString.Print(
            en: "Who issues the /mk commands. Each player: everyone marks themselves from their own "
                + "debuff. Master: exactly one player marks all eight from the priority list, and nobody "
                + "else sends anything. The two can never both be on, so the party numbers cannot be "
                + "fought over.",
            jp: "誰が /mk を撃つか。Each player は各自が自分のデバフから自分に付けます。Master は 1 人が "
                + "優先順位リストから 8 人分を付け、他の誰も送りません。両方 ON にはできないので、"
                + "マーカー番号を奪い合うことはありません。"));

        if (C.MarkerPlacement == MarkerPlacement.Master)
        {
            ImGui.Indent();
            ImGui.TextWrapped(
                "Exactly one player in the party may select this. Markers are placed from the priority "
                + "list once every group is resolvable and the eight slots are distinct. Commands go "
                + "through Splatoon's queue (170ms apart, not sent during duty replay) and are logged. "
                + "The First/Second/Third Target commands below are reused, with <me> replaced by the "
                + "party number.");
            if (C.AssignmentMode is AssignmentMode.PartyMarker)
                ImGui.TextWrapped(InternationalString.Print(
                    en: "Note: with assignment mode Party marker, every client waits for these markers to "
                        + "come back from the server before it can resolve, so the result depends on marker "
                        + "latency. Assignment mode Priority resolves locally and uses the markers only as a "
                        + "display for the humans.",
                    jp: "注意: 割り当てモードが Party marker のときは、各クライアントがこのマーカーが"
                        + "サーバから返ってくるのを待ってから解決するため、結果がマーカーの遅延に依存します。"
                        + "Priority なら各自が手元で解決し、マーカーは人が見るためだけのものになります。"));
            ImGui.Checkbox("Clear markers before placing", ref C.IsMasterClearFirst);
            DrawCommand("Clear command ({0} = party number)", ref C.IsMasterClearCommand);
            ImGui.Unindent();
        }

        if (C.MarkerPlacement == MarkerPlacement.EachPlayer)
        {
            ImGui.Spacing();
            var source = (int)C.MarkerCommandSource;
            if (DrawCombo("Marker command source", ref source, MarkerCommandSourceNames, 180f))
                C.MarkerCommandSource = (MarkerCommandSource)Math.Clamp(source, 0, MarkerCommandSourceNames.Length - 1);
            DrawFloat("Marker delay min (s)", ref C.MarkerDelayMinSeconds);
            DrawFloat("Marker delay max (s)", ref C.MarkerDelayMaxSeconds);
            if (C.MarkerCommandSource == MarkerCommandSource.AccretionDebuff)
                DrawCommand("Accretion command", ref C.AccretionCommand);
            else
                ImGui.Checkbox("Skip target marker on Accretion/Faded Accretion", ref C.SkipTargetMarkerOnAccretion);
        }

        // 対象マーカーのコマンド文字列は両モードが使う。Master は <me> を <番号> に差し替える。
        if (C.MarkerPlacement == MarkerPlacement.Master ||
            (C.MarkerPlacement == MarkerPlacement.EachPlayer &&
             C.MarkerCommandSource == MarkerCommandSource.TargetDebuff))
        {
            ImGui.Spacing();
            DrawCommand("First Target command", ref C.FirstTargetCommand);
            DrawCommand("Second Target command", ref C.SecondTargetCommand);
            DrawCommand("Third Target command", ref C.ThirdTargetCommand);
        }
        ImGui.Unindent();
    }

    private void DrawVisualSettings()
    {
        if (!ImGui.CollapsingHeader("Visuals")) return;

        ImGui.Indent();
        ImGui.TextWrapped(InternationalString.Print(
            en: "Navigation color 1/2 are the gradient colors used by navigation markers and their tether. Set both colors to the same value for a solid color. Tether colors are used for the Black Hole tether line.",
            jp: "Navigation color 1/2 はナビ表示とナビから出るテザーに使うグラデーションの色です。同じ色を2つ設定すると単色表示になります。Tether color はブラックホールのテザー線に使います。"));
        DrawColor("Navigation color 1", ref C.RainbowNavigationColor1);
        DrawColor("Navigation color 2", ref C.RainbowNavigationColor2);
        DrawColor("Correct tether color", ref C.CorrectTetherColor);
        DrawColor("Wrong tether color", ref C.WrongTetherColor);
        DrawColor("Unknown tether color", ref C.UnknownTetherColor);
        ImGui.Unindent();
    }

    private void DrawDisplayTextSettings()
    {
        if (!ImGui.CollapsingHeader("Display text")) return;

        ImGui.Indent();
        ImGui.TextWrapped(InternationalString.Print(
            en: "These fields change only the text shown on Splatoon overlays. Turning a text off hides that text only; it does not disable the marker, tether line, assignment logic, marker commands, or Black Hole detection.",
            jp: "ここはSplatoon上に表示する文言だけを変更します。チェックをOFFにすると文字だけ非表示になります。マーカー、線、割り当てロジック、マーカーコマンド、ブラックホール検出は無効になりません。"));

        DrawSubsection("Line navigation");
        ImGui.Indent();
        DrawText("First line window", C.FirstLineWindowText, ref C.ShowFirstLineWindowText);
        DrawText("Next line", C.NextLineWindowText, ref C.ShowNextLineWindowText);
        DrawText("Take line now", C.TakeLineNowText, ref C.ShowTakeLineNowText);
        DrawText("Unknown slot", C.UnknownSlotText, ref C.ShowUnknownSlotText);
        DrawText("Overlay", C.OverlayText, ref C.ShowOverlayText);
        ImGui.Unindent();

        DrawSubsection("Final sequence");
        ImGui.Indent();
        DrawText("Final center", C.FinalCenterText, ref C.ShowFinalCenterText);
        DrawText("Final role split bait", C.FinalRoleSplitText, ref C.ShowFinalRoleSplitText);
        DrawText("Final role spread", C.FinalSpreadText, ref C.ShowFinalSpreadText);
        DrawText("Final stack", C.FinalStackText, ref C.ShowFinalStackText);
        DrawText("Final tower", C.FinalTowerText, ref C.ShowFinalTowerText);
        DrawText("Final move", C.FinalMoveText, ref C.ShowFinalMoveText);
        ImGui.Unindent();
        ImGui.Unindent();
    }

    private void DrawDebugStatus()
    {
        ImGui.Separator();
        if (!ImGui.CollapsingHeader("Debug status")) return;

        // ブラックホールの並び順の起点。ケフカを掴めていなければ北に落ちている。
        var orderAnchor = C.BlackHoleOrderAnchor switch
        {
            BlackHoleOrderAnchor.KefkaPosition when TryGetKefkaPosition(out var pos) => KefkaAnchorDebugText(pos),
            BlackHoleOrderAnchor.KefkaPosition => "Kefka missing -> N",
            _ => "Arena north"
        };

        // 盤面にいるブラックホール実体と、その線の相手。
        var actorEntries = new List<string>();
        foreach (var obj in Svc.Objects)
        {
            if (obj is not ICharacter character || obj.BaseId != BlackHoleDataId)
                continue;
            var hasBucket = TryBucket(obj.Position, out var bucket);
            actorEntries.Add($"{DirectionName(hasBucket ? bucket : -1)}:{obj.EntityId:X8}" +
                             $"@({obj.Position.X:F1},{obj.Position.Z:F1}) in={hasBucket} " +
                             $"tethers=[{DescribeTethers(character)}]");
        }
        var actors = actorEntries.Count == 0 ? "none" : string.Join(" | ", actorEntries);

        // 線を持っている全キャラ。実体の取りこぼしを見つけるための保険。
        var holderEntries = new List<string>();
        foreach (var obj in Svc.Objects)
        {
            if (obj is not ICharacter character)
                continue;
            var tethers = DescribeTethers(character);
            if (tethers == "none")
                continue;
            holderEntries.Add($"{obj.Name}(0x{obj.EntityId:X8}) data={obj.BaseId} tethers=[{tethers}]");
        }
        var holders = holderEntries.Count == 0 ? "none" : string.Join(" | ", holderEntries.Take(16));

        // アンカー候補。* が現在採用しているもの。
        var candidates = new List<string>();
        var index = 0;
        foreach (var obj in Svc.Objects)
        {
            if (IsKefkaAnchorObject(obj))
            {
                var selected = obj.EntityId == _kefkaId ? "*" : "";
                var pos = FlatPosition(obj.Position);
                var distance = Vector2.Distance(new Vector2(pos.X, pos.Z), new Vector2(Center.X, Center.Z));
                var angle = DirectionAngle(pos) * 180.0f / MathF.PI;
                var visible = obj is ICharacter character && character.IsCharacterVisible();
                var targetable = obj.Struct()->GetIsTargetable();
                var rotation = obj.Rotation * 180.0f / MathF.PI;
                var goid = (ulong)obj.Struct()->GetGameObjectId();
                candidates.Add($"{selected}{index}:{obj.EntityId}/{obj.EntityId:X8}/go={goid:X}" +
                               $"@({pos.X:F1},{pos.Z:F1}) r={distance:F1} a={angle:F0} rot={rotation:F0} " +
                               $"vis={visible} tar={targetable}");
            }
            index++;
        }
        var kefkaCandidates = candidates.Count == 0
            ? "none"
            : candidates.Count <= 24
                ? string.Join(" ", candidates)
                : $"{string.Join(" ", candidates.Take(24))} ... +{candidates.Count - 24}";

        // CenterBait の凍結状態。anchor が "-" ならケフカ位置が取れておらず北向き固定、
        // role が "-" なら優先順位リストから引けておらず戦闘職ロールで表示している。
        var frozen = (_finalInitialAnchorAngle is { } frozenAngle ? $"anchor {Deg(frozenAngle):F1}" : "anchor -") +
                     (_finalInitialStackRole == FinalStackRole.Unknown
                         ? " role -"
                         : $" role {_finalInitialStackRole} owner {Describe(_finalInitialRoleOwner)}");

        var towers = _finalTowerPositions.Count == 0
            ? "none"
            : string.Join(" ", _finalTowerPositions.Select(
                position => $"({position.X:F2},{position.Z:F2})@{Deg(DirectionAngle(position)):F0}"));

        ImGui.Indent();
        ImGui.TextUnformatted($"State={_state} Window={_currentWindow} Slot={_selfSlot} Source={_quality} Guide={_guideKind}");
        ImGui.TextUnformatted($"BlackHoleOrder={C.BlackHoleSourceOrder} Anchor={orderAnchor}");
        // 窓ごとの凍結。anchor が "-" なら線がそろう前かケフカ未捕捉、firstPair が "-" なら未決定
        ImGui.TextUnformatted("WindowFrozen=" +
            $"anchor {(_windowOrderAnchorAngle is { } frozenAnchor ? $"{Deg(frozenAnchor):F1}" : "-")} " +
            $"firstPair {(_windowFirstPair is { } pair ? $"A1={DirectionName(pair.First)} A2={DirectionName(pair.Second)}" : "-")}");
        ImGui.TextWrapped($"BlackHoleExpected={BlackHoleExpectedDebugText("settings")}");
        ImGui.TextWrapped($"BlackHoleActors={actors}");
        ImGui.TextWrapped($"TetherHolders={holders}");
        ImGui.TextWrapped($"KefkaCandidates={kefkaCandidates}");
        ImGui.TextUnformatted($"Final={_finalStage} Landing={_landingCount} Markers={_finalStackMarkerCount} FirstStack={_firstFinalStackRole} SecondStack={_secondFinalStackRole} CurrentStack={_currentFinalStackRole}");
        ImGui.TextUnformatted($"FinalDondokoHits={_finalDondokoHitCount}");
        ImGui.TextUnformatted($"FinalInitialFrozen={frozen}");
        ImGui.TextUnformatted($"FinalPairAnchor=kefka {KefkaAnchorDebugText()} Towers={towers}");
        if (!string.IsNullOrWhiteSpace(_guideDebug))
            ImGui.TextUnformatted(_guideDebug);
        ImGui.Unindent();
    }

    #endregion

    #region private methods : state transition
    /********************************************************************/
    /* private methods : state transition                               */
    /********************************************************************/
    // _state を進める処理と、自機が差し替わったとき (BasePlayer override) の再解決。
    /// <summary>割り当て収集フェーズに入る。前回が完了済みなら状態を巻き戻してから入る。</summary>
    private void StartCollection()
    {
        if (_state == State.Completed)
        {
            ClearMechanicState(clearSlot: true);
            _state = State.Idle;
        }

        if (_state == State.Idle)
            _state = State.CollectingAssignments;
    }

    /// <summary>ブラックホールフェーズに入る。ウィンドウを 0 に戻し、
    /// 線・レーン・自分の担当・誘導のキャッシュをすべて捨てる。</summary>
    /// <summary>自機が差し替わったか (duty replay の Base Player Override) を検出し、
    /// 変わっていれば自分の解決結果と送信待ちのマーカーを捨てて解き直させる。</summary>
    private void RefreshBasePlayerState()
    {
        var id = BasePlayer?.EntityId ?? 0;
        if (_selfPlayerId == id) return;

        var previousId = _selfPlayerId;
        _selfPlayerId = id;
        if (previousId == 0) return;

        ClearSelfResolution();
        _pendingMarkerCommand = "";
        _markerCommandAtMs = 0;
        _sentMarkerCommand = false;

        // 見る人が変わったので、終盤の誘導は新しい人のロールで出し直す。
        // 実プレイでは起きない。duty replay の Base Player Override 専用の経路。
        if (_state != State.FinalSequence)
            return;

        switch (_finalStage)
        {
            case FinalStage.CenterBait:
                if (TryGetFinalInitialBaitGuide(out var destination, out var text))
                    SetGuide(destination, text, GuidanceKind.FinalCenter, LateP3Blizzaga, 0.0f, 0.0f);
                else
                    SetInstruction(TextOrEmpty(C.ShowFinalCenterText, C.FinalCenterText), GuidanceKind.FinalCenter);
                break;
            case FinalStage.RoleSpread:
                SetFinalRoleGuide(C.ShowFinalSpreadText, C.FinalSpreadText, GuidanceKind.FinalSpread, LateP3Blizzaga);
                break;
            case FinalStage.Landing1:
            case FinalStage.Landing2:
                if (_currentFinalStackRole != FinalStackRole.Unknown)
                    SetFinalLanding(_currentFinalStackRole);
                break;
            case FinalStage.ProtrudeMove:
                SetFinalRoleGuide(C.ShowFinalMoveText, C.FinalMoveText, GuidanceKind.FinalMove, Protrude);
                break;
        }
    }

    #endregion

    #region private methods : slot resolution
    /********************************************************************/
    /* private methods : slot resolution                                */
    /********************************************************************/
    // 「自分がどのスロットか」を決める中核。TryResolveSlot が入口で、
    // 設定の AssignmentMode に応じて、頭上マーカー / 優先順位リスト / ロール+Accretion に分岐する。
    //
    // 注意: ここは自分の分しか解かない。他人の解決結果を保持する入れ物が無いため、
    // 2 人が同じスロットに解決しても検出できない (上流からの既知の弱点)。
    /// <summary>自分のスロットが未解決なら 1 度だけ解く。解けなければ「未確定」を表示する。
    /// 既に解決済み (<c>_selfSlot != Slot.None</c>) なら何もしない。</summary>
    private void ResolveSelfSlot()
    {
        var me = BasePlayer;
        if (me == null || _selfSlot != Slot.None || !_groups.ContainsKey(me.EntityId)) return;
        if (TryResolveSlot(me, out _selfSlot, out _quality))
            _instruction = "";
        else
            _instruction = TextOrEmpty(C.ShowUnknownSlotText, C.UnknownSlotText);
    }

    private void ClearSelfResolution()
    {
        _selfSlot = Slot.None;
        _quality = AssignmentQuality.Unknown;
        ClearSelfDisplayState();
    }

    private void ClearSelfDisplayState()
    {
        _selfBlackHoleTask = null;
        _selfCompletedWindow = -1;
        _instruction = "";
    }

    /// <summary>マーカー由来のスロット解決だけを捨てて、次フレームの
    /// <see cref="ResolveSelfSlot"/> に引き直させる。</summary>
    /// <remarks>ResolveSelfSlot は一度解決したスロットを latch して二度と見直さない。
    /// そのため頭上マーカーが揃う前に解決してしまうと、そのギミックのあいだ間違ったままになる。
    /// 前のウィンドウのマーカーが残っている状態で対象デバフが来ると、TryResolveSlot が
    /// 古い方を読み、本来の割り当てとの競争に勝ってしまう。自分のマーカーコマンドにも
    /// 0.1〜0.8 秒の遅延がある以上、この競争は普通に起きる。
    ///
    /// <see cref="ClearSelfResolution"/> と違い _selfCompletedWindow と _selfBlackHoleTask は
    /// 残す。ウィンドウの途中でそれらを消すと、本人が既に終えたウィンドウの誘導を
    /// もう一度出してしまうため。</remarks>
    private void InvalidateMarkerResolution()
    {
        if (_quality != AssignmentQuality.Marker)
            return;
        if (_state is not (State.CollectingAssignments or State.BlackHoleActive))
            return;

        _selfSlot = Slot.None;
        _quality = AssignmentQuality.Unknown;
    }

    /// <summary>指定プレイヤーのスロットを、設定の <see cref="AssignmentMode"/> に従って決める。</summary>
    /// <remarks>引数でプレイヤーを取るので全員分を解けるが、実際の呼び出しは自機と
    /// <c>TryFirstPairBucket</c> 内の 1 人だけ。他人の結果を保持する入れ物が無いため、
    /// スロットの衝突は検出されない。</remarks>
    private bool TryResolveSlot(IPlayerCharacter player, out Slot slot, out AssignmentQuality quality)
    {
        quality = AssignmentQuality.Unknown;
        var group = _groups.GetValueOrDefault(player.EntityId);
        if (group == TargetGroup.None)
        {
            slot = Slot.None;
            return false;
        }

        if (C.AssignmentMode is AssignmentMode.RoleAccretion or AssignmentMode.FixedRoleAccretion or AssignmentMode.FixedMarkerLanes)
        {
            if (HasCompleteGroups() && _accretionPlayers.Count >= 2)
            {
                // Accretion 持ちが 3 番手。残りはロールと FirstOrbRole の設定で 1/2 番手に割れる。
                // FixedRoleAccretion のときは順位ではなく設定したマーカー番号をそのまま使う。
                var isAccretion = _accretionPlayers.Contains(player.EntityId);
                var isSupport = player.GetRole() is CombatRole.Tank or CombatRole.Healer;
                var supportFirst = C.FirstOrbRole == FirstOrbRole.Support;
                var rank = C.AssignmentMode == AssignmentMode.FixedRoleAccretion
                    ? isAccretion ? (int)C.AccretionMarker : isSupport ? (int)C.SupportMarker : (int)C.DpsMarker
                    : isAccretion ? 2 : isSupport == supportFirst ? 0 : 1;
                slot = SlotFromRank(group, rank);
                if (slot != Slot.None)
                {
                    quality = AssignmentQuality.RoleAccretion;
                    return true;
                }
            }
            slot = Slot.None;
            return false;
        }

        if (C.AssignmentMode == AssignmentMode.PartyMarker)
        {
            // 頭上マーカーを 1 番目に見つけたものが順位。MarkerLineOrders でレーンに写す。
            slot = Slot.None;
            for (var i = 0; i < SelectableMarkerIds.Length; i++)
                if (Marking.HaveMark(player, (uint)SelectableMarkerIds[i]))
                {
                    slot = SlotFromRank(group, C.MarkerLineOrders[i]);
                    break;
                }
            if (slot != Slot.None)
            {
                quality = AssignmentQuality.Marker;
                return true;
            }
        }

        if (C.AssignmentMode == AssignmentMode.Priority && HasCompleteGroups() && TryPrioritySlot(player, group, out slot))
        {
            quality = AssignmentQuality.Priority;
            return true;
        }

        slot = Slot.None;
        return false;
    }

    private bool TryPrioritySlot(IPlayerCharacter player, TargetGroup group, out Slot slot)
    {
        var players = C.PriorityData.GetPlayers(x =>
            x.IGameObject is IPlayerCharacter pc && _groups.GetValueOrDefault(pc.EntityId) == group);
        var rank = players?.FindIndex(x => x.IGameObject.EntityId == player.EntityId) ?? -1;
        slot = rank < 0 ? Slot.None : SlotFromRank(group, rank);
        return slot != Slot.None;
    }

    #endregion

    #region private methods : black hole tether
    /********************************************************************/
    /* private methods : black hole tether                              */
    /********************************************************************/
    // ブラックホール実体と線の観測・キャッシュ。毎フレーム PollLiveBlackHoleTethers から回る。
    // 観測できるものは観測し、足りない分を bucket (方角 0..3) に量子化して扱う。
    private void RefreshExpectedTether()
    {
        var expected = ExpectedBucket(_selfSlot);
        if (_state == State.BlackHoleActive && _selfCompletedWindow == _currentWindow)
        {
            if (SelfTetherBucket >= 0)
                ClearSelfTether(false);
            else
                _instruction = "";
            return;
        }

        if (expected >= 0 && _hitSources.Contains(expected))
        {
            _selfCompletedWindow = _currentWindow;
            ClearSelfTether(false);
            return;
        }

        if (expected < 0 ||
            !_tetherSources.TryGetValue(expected, out var source) ||
            !_tetherTargets.TryGetValue(expected, out var target))
        {
            if (SelfTetherBucket >= 0)
                ClearSelfTether(false);
            _instruction = LineWindowInstruction();
            return;
        }

        var flatSource = FlatPosition(source);
        _selfBlackHoleTask = new BlackHoleTask(expected, flatSource, target, StandPosition(flatSource));
        _instruction = LineWindowInstruction();
        return;

        // 線の始点から見た立ち位置。まず基準角 (±45 度) と半径で候補を作り、ブラックホールに
        // 近すぎる場合は半径と角度を刻んで総当たりで離れた点を探す。見つからなければ最も
        // 離れていた候補を返す (必ず何かを返す)。
        Vector3 StandPosition(Vector3 standSource)
        {
            var direction = _currentWindow == 0 && C.FirstWindowBaitDirection != FirstWindowBaitDirection.SameAsLineBaitDirection
                ? C.FirstWindowBaitDirection == FirstWindowBaitDirection.Counterclockwise
                    ? LineBaitDirection.Counterclockwise
                    : LineBaitDirection.Clockwise
                : C.LineBaitDirection;
            var side = direction == LineBaitDirection.Counterclockwise ? -1.0f : 1.0f;
            var radius = _currentWindow == 9 ? 19.0f : 9.021f;
            var angle = DirectionAngle(standSource) + side * MathF.PI / 4.0f;
            var best = PositionFromDirectionAngle(angle, radius);
            var bestDistance = NearestBlackHoleDistanceSq(best);
            if (bestDistance >= BlackHoleAvoidRadiusSq)
                return best;

            for (var inward = 0; inward <= 6; inward++)
            {
                var candidateRadius = Math.Max(6.0f, radius - inward * 1.0f);
                for (var step = inward == 0 ? 1 : 0; step <= 4; step++)
                {
                    if (step == 0)
                    {
                        if (Consider(angle, candidateRadius))
                            return best;
                    }
                    else
                    {
                        var offset = step * (MathF.PI / 24.0f);
                        if (Consider(angle + side * offset, candidateRadius))
                            return best;
                        if (Consider(angle - side * offset, candidateRadius))
                            return best;
                    }
                }
            }

            return best;

            bool Consider(float candidateAngle, float candidateRadius)
            {
                var candidate = PositionFromDirectionAngle(candidateAngle, candidateRadius);
                var distance = NearestBlackHoleDistanceSq(candidate);
                if (distance <= bestDistance)
                    return false;
                best = candidate;
                bestDistance = distance;
                return bestDistance >= BlackHoleAvoidRadiusSq;
            }
        }

        float NearestBlackHoleDistanceSq(Vector3 candidate)
        {
            var nearest = float.MaxValue;
            foreach (var position in _blackHolePositions)
                nearest = Math.Min(nearest, Vector3.DistanceSquared(candidate, position));
            return nearest;
        }
    }

    /// <summary>第1/第2/第3対象がそれぞれ 3/3/2 人そろっているか。
    /// 唯一の完全性検査だが、呼ばれるのは Priority と RoleAccretion 系のみで、
    /// 既定の PartyMarker モードでは通らない。</summary>
    private bool HasCompleteGroups()
    {
        return _groups.Count(x => x.Value == TargetGroup.Attack) == 3 &&
               _groups.Count(x => x.Value == TargetGroup.Bind) == 3 &&
               _groups.Count(x => x.Value == TargetGroup.Stop) == 2;
    }

    private void ClearCurrentWindowTethers()
    {
        _tetherTargets.Clear();
        _tetherSources.Clear();
        _selfCompletedWindow = -1;
        _windowOrderAnchorAngle = null;
        _windowFirstPair = null;
    }

    /// <summary>毎フレーム、生存中のブラックホールと現在張られている線を観測し直す。
    /// 前フレームのキャッシュは破棄するので、消えた線は自動的に落ちる。</summary>
    private void PollLiveBlackHoleTethers()
    {
        CacheLiveBlackHoleActors();
        _tetherTargets.Clear();
        _tetherSources.Clear();

        foreach (var obj in Svc.Objects)
        {
            if (obj is not ICharacter character)
                continue;

            var chr = character.Struct();
            for (var i = 0; i < chr->Vfx.Tethers.Length; i++)
            {
                var tether = chr->Vfx.Tethers[i];
                if (tether.Id == 0) continue;

                var target = Svc.Objects.FirstOrDefault(x => x.GameObjectId == tether.TargetId);
                var targetId = target?.EntityId ?? tether.TargetId.ObjectId;
                if (targetId == 0)
                    continue;

                // 線の両端のうち、ブラックホール側がどちらかは決まっていない。両方試す。
                uint tetherTarget;
                if (TryResolveBlackHoleEndpoint(character.EntityId, out var blackHolePosition, out var bucket))
                    tetherTarget = targetId;
                else if (TryResolveBlackHoleEndpoint(targetId, out blackHolePosition, out bucket))
                    tetherTarget = character.EntityId;
                else
                    continue;

                _tetherTargets[bucket] = tetherTarget;
                _tetherSources[bucket] = blackHolePosition;
            }
        }

        // 線がそろったこの瞬間の値で、窓のあいだ動かさないものを確定する。
        // 下の焼き付けも OrderedBuckets を通るので、その前に起点を決めておく。
        FreezeWindowDecisions();

        // FixedRoleAccretion / FixedMarkerLanes は、セット先頭のウィンドウで観測した
        // bucket をレーンごとに焼き付け、そのセットの間ずっと使い回す。
        if (C.AssignmentMode is AssignmentMode.FixedRoleAccretion or AssignmentMode.FixedMarkerLanes)
        {
            var startWindow = FixedMarkerSetStartWindow(_currentWindow);
            if (startWindow >= 0)
            {
                if (_fixedLaneSetStartWindow != startWindow)
                {
                    _fixedLaneSetStartWindow = startWindow;
                    Array.Fill(_fixedLaneSetBuckets, -1);
                }

                // 線が出そろう前に焼き付けると誤った割り当てが固定されるので、本数を待つ。
                if (_currentWindow == startWindow &&
                    _tetherTargets.Count >= ExpectedSourcesByWindow[startWindow])
                    for (var lane = 0; lane < _fixedLaneSetBuckets.Length; lane++)
                        if (_fixedLaneSetBuckets[lane] < 0)
                        {
                            var bucket = C.AssignmentMode == AssignmentMode.FixedRoleAccretion
                                ? ExpectedFixedSpotBucketUncached(lane)
                                : ExpectedMarkerOrFlexLaneBucket(lane);
                            if (bucket >= 0)
                                _fixedLaneSetBuckets[lane] = bucket;
                        }
            }
        }

        RefreshExpectedTether();
    }

    /// <summary>その窓の線が出そろった最初のフレームで、窓のあいだ動かさない値を確定する。</summary>
    /// <remarks>確定するのは 2 つ。A/B/C/D の起点 (巨大ケフカの方角) と、2 本目の窓の距離判定。
    /// どちらも材料が毎フレーム動く (ケフカの補間、攻撃 1 の移動、線の VFX の出入り) ので、
    /// 人が「線を見てケフカを見て決める」のと同じ 1 点で取り、以後は持ち回る。
    /// 窓が進めば <see cref="ClearCurrentWindowTethers"/> が捨て、次の窓で取り直す。
    /// 巨大ケフカは回と回の間に移動するので、フェーズ開始で 1 度だけ取るのでは 2 回目以降がずれる。
    ///
    /// 取れないうちは凍結しない。起点はケフカを掴めていなければ北のまま次フレームに回し、
    /// 距離判定は攻撃 1 が解決できるまで待つ。当てずっぽうを latch しないため。</remarks>
    private void FreezeWindowDecisions()
    {
        if (_currentWindow is < 0 or > 9) return;
        if (_tetherTargets.Count < ExpectedSourcesByWindow[_currentWindow]) return;

        // 起点: 巨大ケフカの方角。設定が盤面北なら凍結するものが無い。
        if (_windowOrderAnchorAngle is null &&
            C.BlackHoleOrderAnchor == BlackHoleOrderAnchor.KefkaPosition &&
            TryGetKefkaPosition(out var kefka))
            _windowOrderAnchorAngle = DirectionAngle(kefka);

        // 2 本目の窓: 攻撃 1 に近い方を攻撃 1、残りを攻撃 2。攻撃 1 は 1 本目の反時計側で
        // 待機しているので、線が 2 本そろった瞬間の位置で決めれば戦略と一致する。
        if (_windowFirstPair is not null || _currentWindow != 1 ||
            C.FirstPairAssignment != FirstPairAssignment.FirstSlotNearest)
            return;

        var activeBuckets = _tetherTargets.Keys.Where(_tetherSources.ContainsKey).ToList();
        if (activeBuckets.Count != 2)
            return;

        IPlayerCharacter? firstPlayer = null;
        foreach (var player in Svc.Objects.OfType<IPlayerCharacter>())
        {
            if (!_groups.ContainsKey(player.EntityId)) continue;
            if (TryResolveSlot(player, out var resolved, out _) && resolved == Slot.Attack1)
            {
                firstPlayer = player;
                break;
            }
        }
        if (firstPlayer == null)
            return;

        var firstPosition = FlatPosition(firstPlayer.Position);
        var first = activeBuckets
            .OrderBy(activeBucket =>
            {
                var source = _tetherSources[activeBucket];
                return Vector2.DistanceSquared(
                    new Vector2(firstPosition.X, firstPosition.Z),
                    new Vector2(source.X, source.Z));
            })
            .ThenBy(activeBucket => activeBucket)
            .First();
        _windowFirstPair = (first, activeBuckets.First(activeBucket => activeBucket != first));
    }

    private void CacheLiveBlackHoleActors()
    {
        _blackHolePositions.Clear();
        foreach (var obj in Svc.Objects)
            if (obj is ICharacter character && character.BaseId == BlackHoleDataId)
                _blackHolePositions.Add(FlatPosition(character.Position));
    }

    private void ClearSelfTether(bool keepWindowText)
    {
        _selfBlackHoleTask = null;
        _instruction = keepWindowText ? LineWindowInstruction() : "";
    }

    private string LineWindowInstruction()
    {
        if (_state != State.BlackHoleActive || _selfSlot == Slot.None || _currentWindow is < 0 or > 9 ||
            _selfCompletedWindow == _currentWindow)
            return "";

        // 現在のウィンドウ以降で、自分に出番があるいちばん近いウィンドウ。無ければ -1。
        var nextWindow = -1;
        for (var window = Math.Max(0, _currentWindow); window <= 9; window++)
            if (ExpectedRank(_selfSlot, window) >= 0)
            {
                nextWindow = window;
                break;
            }

        if (nextWindow < 0)
            return "";
        if (nextWindow == _currentWindow)
            return TextOrEmpty(C.ShowTakeLineNowText, C.TakeLineNowText, _currentWindow + 1);
        if (nextWindow == _currentWindow + 1)
            return TextOrEmpty(C.ShowNextLineWindowText, C.NextLineWindowText, _currentWindow + 1);
        return TextOrEmpty(C.ShowFirstLineWindowText, C.FirstLineWindowText, _currentWindow + 1, nextWindow + 1);
    }

    #endregion

    #region private methods : marker command
    /********************************************************************/
    /* private methods : marker command                                 */
    /********************************************************************/
    // 頭上マーカーの自動設置。ここだけがサーバに届く副作用を持つ。
    //
    // 安全側の作りになっている:
    //   - C.MarkerPlacement が既定 Off のオプトイン
    //   - _sentMarkerCommand による一発ガード
    //   - DutyRecorderPlayback を二重にチェック (キュー時と実行時)
    //   - 0.1〜0.8 秒のランダム遅延で 8 クライアントの同時発火を散らす
    //
    // 経路は 2 つある。どちらも置いたマーカーを読み返さない:
    //   自分の分 : RunMarkerCommand -> QueueMarkerCommand -> ExecutePendingMarkerCommand
    //   全員の分 : TryPlaceMasterMarkers (C.MarkerPlacement == Master のときだけ)
    // 2 つは C.MarkerPlacement の 1 つの値で選ぶので、同時には走らない。
    private void RunMarkerCommand(TargetGroup group)
    {
        if (C.MarkerCommandSource != MarkerCommandSource.TargetDebuff)
            return;
        // Accretion を持っているなら対象マーカーは出さない。出すと本来の
        // 割り当てを上書きしてしまう。送らないが送信済みには倒しておく。
        var skipForAccretion = C.SkipTargetMarkerOnAccretion &&
            (_selfHadAccretionMarkerBlock ||
             _accretionPlayers.Contains(BasePlayer?.EntityId ?? 0) ||
             BasePlayer?.StatusList.Any(status => status.StatusId is AccretionStatus or LineDoneStatus) == true);
        if (skipForAccretion)
        {
            _sentMarkerCommand = true;
            return;
        }

        QueueMarkerCommand(group switch
        {
            TargetGroup.Attack => C.FirstTargetCommand,
            TargetGroup.Bind => C.SecondTargetCommand,
            TargetGroup.Stop => C.ThirdTargetCommand,
            _ => ""
        }, true);
    }

    private void RunAccretionMarkerCommand()
    {
        if (C.MarkerCommandSource == MarkerCommandSource.AccretionDebuff)
            QueueMarkerCommand(C.AccretionCommand);
    }

    private void CancelPendingTargetMarkerCommandForAccretion()
    {
        if (!C.SkipTargetMarkerOnAccretion || C.MarkerCommandSource != MarkerCommandSource.TargetDebuff ||
            !_pendingTargetMarkerCommand)
            return;

        _pendingMarkerCommand = "";
        _markerCommandAtMs = 0;
        _pendingTargetMarkerCommand = false;
    }

    /// <summary>マーカー設置コマンドを送信予約する。実際の送信は
    /// <see cref="ExecutePendingMarkerCommand"/> が遅延後に行う。
    /// 一発ガード・オプトイン・リプレイ判定をここで通す。</summary>
    /// <remarks>リプレイ中も予約はする。遅延も本番と同じだけ待つ。違うのは送る文字列で、
    /// <c>/echo</c> を前置してサーバへ届かない形にしてある。実際に何をいつ送るつもりだったのかを、
    /// リプレイを見ながらチャット欄で確認できるようにするため。
    /// マスターマーカー側は Splatoon のキューがリプレイ中は緑文字で出すので、ここでは扱わない。</remarks>
    private void QueueMarkerCommand(string command, bool targetDebuffCommand = false)
    {
        if (_sentMarkerCommand) return;
        _sentMarkerCommand = true;
        if (C.MarkerPlacement != MarkerPlacement.EachPlayer) return;

        var text = command ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            _pendingMarkerCommand = "";
            _pendingTargetMarkerCommand = false;
            return;
        }

        // リプレイ中は実際には送らず、送るはずだった内容を /echo でチャット欄に出す。
        // 判断は予約時に固定して文字列へ畳み込む。実行までにリプレイ状態が変わっても、
        // 予約した時の意図と違うもの (実コマンド) に化けさせないため。
        _pendingMarkerCommand = Svc.Condition[ConditionFlag.DutyRecorderPlayback] ? $"/echo {text}" : text;
        _pendingTargetMarkerCommand = targetDebuffCommand;

        // 0.1〜0.8 秒のランダム遅延。8 クライアントが同時に撃つのを散らすため。
        // 設定が負や逆順でも落ちないように、ここで潰してから使う。
        var minSeconds = Math.Max(0.0f, C.MarkerDelayMinSeconds);
        var maxSeconds = Math.Max(0.0f, C.MarkerDelayMaxSeconds);
        if (maxSeconds < minSeconds)
            (minSeconds, maxSeconds) = (maxSeconds, minSeconds);
        var seconds = minSeconds + (float)Random.Shared.NextDouble() * (maxSeconds - minSeconds);
        _markerCommandAtMs = Environment.TickCount64 + (long)MathF.Round(seconds * 1000.0f);
    }

    /// <summary>予約時刻を過ぎていれば実際にコマンドを送る。**ここだけがサーバに届く**。
    /// 送信直前にもリプレイ判定を行う。</summary>
    private void ExecutePendingMarkerCommand()
    {
        if (_markerCommandAtMs <= 0 || Environment.TickCount64 < _markerCommandAtMs) return;

        var command = _pendingMarkerCommand;
        _pendingMarkerCommand = "";
        _pendingTargetMarkerCommand = false;
        _markerCommandAtMs = 0;
        if (string.IsNullOrWhiteSpace(command)) return;

        // 予約時にリプレイなら /echo へ倒してある。ここへ実コマンドのまま来るのは、
        // 予約した後にリプレイが始まった場合だけ。その 1 通りだけを最後に弾く。
        if (Svc.Condition[ConditionFlag.DutyRecorderPlayback] &&
            !command.StartsWith("/echo ", StringComparison.OrdinalIgnoreCase))
            return;

        Chat.ExecuteCommand(command);
    }

    /// <summary>優先順位リストから 8 人分の頭上マーカーを置く。PartyMarker モードが読む配置を
    /// そのまま再現する。マスター 1 人だけが実行する。</summary>
    /// <remarks>置いたマーカーは誰も読み返さない。各クライアントは自分で優先順位リストから
    /// 解決しているので、マーカーが遅れても欠けても誰の指示も変わらない。マーカーは人間用。
    ///
    /// ギミック 1 回につき 1 度だけ、しかも 8 人全員のグループが引けて 8 スロットが
    /// 重複なく出そろったときにだけ走る。優先順位リストに矛盾があれば 1 つも置かない。
    /// 途中まで置いて残りを諦める、という中途半端な状態を作らないため。
    ///
    /// グループ内はランク順にコマンドを出す。裸の "/mk attack" は空いている最小の番号を
    /// 割り当てるため、発行順がそのまま番号順になる。公式スクリプト 75 例はすべて裸形で、
    /// 番号付きの形を使っているものは無い。番号付きが実機で通るなら、コマンド文字列を
    /// そちらに変えれば発行順は関係なくなる。</remarks>
    private void TryPlaceMasterMarkers()
    {
        if (C.MarkerPlacement != MarkerPlacement.Master || _placedMasterMarkers) return;
        if (_state is not (State.CollectingAssignments or State.BlackHoleActive)) return;

        var order = new List<(int Rank, TargetGroup Group, uint EntityId)>();
        foreach (var pc in FakeParty.Get())
        {
            var group = _groups.GetValueOrDefault(pc.EntityId);
            if (group == TargetGroup.None) return;                  // まだ全員にデバフが付いていない
            if (!TryPrioritySlot(pc, group, out var slot)) return;   // 優先順位リストがまだ引けない
            order.Add((RankFromSlot(slot), group, pc.EntityId));
        }

        if (order.Count != 8) return;
        if (order.Select(x => (x.Group, x.Rank)).Distinct().Count() != 8)
        {
            PluginLog.Warning("[P3_Earthquake] priority produced duplicate slots; placing nothing");
            return;
        }

        var tags = new Dictionary<uint, int>();
        foreach (var entry in order)
        {
            // EntityId から <1>..<8> のパーティ番号を引く。
            // duty recorder 再生中は ExtendedPronoun がオブジェクトテーブル順で返すため、
            // 実プレイのパーティ番号とは一致しない。
            var tag = -1;
            for (var i = 1; i <= 8; i++)
            {
                var obj = FakePronoun.Resolve($"<{i}>");
                if (obj != null && obj->EntityId == entry.EntityId)
                {
                    tag = i;
                    break;
                }
            }
            if (tag == -1) return;                                   // パーティ番号がまだ引けない
            tags[entry.EntityId] = tag;
        }

        _placedMasterMarkers = true;

        if (C.IsMasterClearFirst)
            foreach (var tag in tags.Values.OrderBy(x => x))
                EnqueueMasterCommand(string.Format(C.IsMasterClearCommand, tag));

        foreach (var entry in order.OrderBy(x => x.Group).ThenBy(x => x.Rank))
        {
            var command = entry.Group switch
            {
                TargetGroup.Attack => C.FirstTargetCommand,
                TargetGroup.Bind => C.SecondTargetCommand,
                TargetGroup.Stop => C.ThirdTargetCommand,
                _ => ""
            };
            if (command.Length == 0) continue;
            EnqueueMasterCommand(command.Replace("<me>", $"<{tags[entry.EntityId]}>"));
        }
    }

    /// <summary>マスターのコマンドを Splatoon のキューに積む。</summary>
    /// <remarks>リプレイ判定・メッセージ間 170ms の間隔・リセット時のキャンセルは
    /// <c>DangerousEnqueueCommand</c> 側が面倒を見るので、ここでは繰り返さない。
    /// 何を送ったかは常にログに残す。8 人分がまとめて飛ぶため、後から順番を追えないと困る。</remarks>
    private void EnqueueMasterCommand(string command)
    {
        PluginLog.Information($"[P3_Earthquake] master marker: {command}");
        Controller.DangerousEnqueueCommand(command, false);
    }

    #endregion

    #region private methods : window advance
    /********************************************************************/
    /* private methods : window advance                                 */
    /********************************************************************/
    /// <summary>着弾した bucket を受けてウィンドウを進める。
    /// その bucket が自分の担当 (実際に取った線か、期待値) なら当該ウィンドウを完了とみなす。</summary>
    private void AdvanceWindow(int sourceBucket)
    {
        if (_state != State.BlackHoleActive || _currentWindow is < 0 or > 9) return;
        var expected = ExpectedBucket(_selfSlot);
        if (_selfCompletedWindow != _currentWindow &&
            (sourceBucket == SelfTetherBucket || expected >= 0 && sourceBucket == expected))
        {
            _selfCompletedWindow = _currentWindow;
            ClearSelfTether(false);
        }

        _hitSources.Add(sourceBucket);
        if (_hitSources.Count < ExpectedSourcesByWindow[_currentWindow]) return;

        if (SelfTetherBucket >= 0 && _hitSources.Contains(SelfTetherBucket))
            ClearSelfTether(true);
        _hitSources.Clear();
        ClearCurrentWindowTethers();
        _currentWindow++;
        if (_currentWindow > 9)
            EnterFinalSequence();
    }

    #endregion

    #region private methods : final sequence
    /********************************************************************/
    /* private methods : final sequence                                 */
    /********************************************************************/
    // 終盤 (Blizzaga 以降) の誘導。中央誘導 -> ロール散開 -> 着地 -> 塔 -> 散開移動。
    // 進行は _finalStage と複数のカウンタで管理される。
    private void EnterFinalSequence()
    {
        if (_state == State.Completed) return;

        _state = State.FinalSequence;
        if (_finalStage == FinalStage.None)
            _finalStage = FinalStage.AwaitingBlizzaga;
        _currentWindow = Math.Max(_currentWindow, 10);
        _instruction = "";
        ClearSelfTether(false);
        ClearBlackHoleState();
    }

    private bool TryGetFinalInitialBaitGuide(out Vector3 destination, out string text)
    {
        if (C.FinalInitialBaitMode == FinalInitialBaitMode.Center)
        {
            destination = Center;
            text = TextOrEmpty(C.ShowFinalCenterText, C.FinalCenterText);
            return true;
        }

        // ロールは「誰の」ロールかまで込みで凍結する。リプレイの Base Player Override で見る人が
        // 変われば別人のロールを出さなければならないので、持ち主が変わったら引き直す。
        // 優先順位リストから引けた値だけを凍結し、戦闘職ロールのフォールバックは凍結しない。
        // 後からリストが引けるようになったとき、そちらへ上書きできるようにするため。
        var owner = BasePlayer?.EntityId ?? 0;
        FinalStackRole ownStackRole;
        if (owner != 0 && owner == _finalInitialRoleOwner && _finalInitialStackRole != FinalStackRole.Unknown)
        {
            ownStackRole = _finalInitialStackRole;
        }
        else if (TryGetOwnRolePosition(out var rolePosition) &&
                 StackRoleFromRolePosition(rolePosition) != FinalStackRole.Unknown)
        {
            _finalInitialRoleOwner = owner;
            _finalInitialStackRole = StackRoleFromRolePosition(rolePosition);
            ownStackRole = _finalInitialStackRole;
        }
        else
        {
            ownStackRole = BasePlayer == null
                ? FinalStackRole.Unknown
                : StackRoleFromCombat(BasePlayer.GetRole());
        }

        if (ownStackRole == FinalStackRole.Unknown)
        {
            destination = default;
            text = "";
            return false;
        }

        var northRole = C.FinalInitialNorthRole == FinalInitialNorthRole.Support
            ? FinalStackRole.Support
            : FinalStackRole.Dps;
        var goNorth = ownStackRole == northRole;
        var angle = NormalizeAngle(FreezeFinalInitialAnchorAngle() + (goNorth ? 0.0f : MathF.PI));
        destination = PositionFromDirectionAngle(angle, 5.5f);
        text = TextOrEmpty(C.ShowFinalRoleSplitText, C.FinalRoleSplitText, ownStackRole == FinalStackRole.Support ? "Support" : "DPS");
        return true;
    }

    /// <summary>CenterBait の基準となるケフカの向きを、突入時の値で確定させる。</summary>
    /// <remarks>CenterBait の誘導位置は「ケフカの向き」と「自分のロール」の 2 つだけで決まる。
    /// どちらも実行中に値が動くため、凍結しないと同じ CenterBait のあいだに位置が飛ぶ。
    ///
    /// ケフカの向きが動く経路は 2 つ。_kefkaId が確定していると
    /// <see cref="RefreshKefkaAnchorFromObject"/> が毎フレーム実体の現在位置へ追従させる。
    /// また詠唱が来るたび <see cref="UpdateKefkaAnchor"/> が、実体 / クローンとの回転一致 /
    /// 回転から作った仮想点、のどれかへ出所を乗り換えることがある。
    ///
    /// さらに詠唱通知は OnStartingCast の 2 つのオーバーロード経由で 1 回の詠唱につき 2 回
    /// 届き、しかも同一フレームとは限らない。この 2 回のあいだに上記が動くのが実害だった。
    ///
    /// 値が取れないうちは凍結しない。北向き固定という当てずっぽうを latch すると二度と
    /// 直せなくなるので、確かな値が来た最初の 1 回だけを確定させる。</remarks>
    private float FreezeFinalInitialAnchorAngle()
    {
        if (_finalInitialAnchorAngle is { } frozen)
            return frozen;

        // ケフカ位置が無いあいだは KefkaAnchorAngle() と同じく北 (0 度) を返すだけで凍結しない。
        if (!TryGetKefkaPosition(out var kefka))
            return 0.0f;

        var angle = DirectionAngle(kefka);
        _finalInitialAnchorAngle = angle;
        return angle;
    }

    /// <summary>CenterBait の南北を決める自分のロールを、優先順位リストから引けた値で確定させる。</summary>
    /// <remarks>優先順位リストから引けなかったフレームは <see cref="OwnFinalStackRole"/> と同じく
    /// 戦闘職ロールへ落ちる。設定ロールと食い違う編成だと Support/DPS が入れ替わり 180 度飛ぶ。
    /// そのためリストから引けた値だけを凍結し、フォールバックは凍結しない。後からリストが
    /// 引けるようになったとき、そちらへ上書きできるようにするため。
    ///
    /// 凍結は「誰の」ロールかまで込みで持つ。リプレイの Base Player Override で見る人が
    /// 変われば別人のロールを出さなければならないので、持ち主が変わったら引き直す。</remarks>
    private void RecordFinalStackRole(FinalStackRole stackRole)
    {
        if (stackRole == FinalStackRole.Unknown)
            return;
        _finalStackMarkerCount++;
        if (_firstFinalStackRole == FinalStackRole.Unknown)
            _firstFinalStackRole = stackRole;
        else if (_secondFinalStackRole == FinalStackRole.Unknown)
            _secondFinalStackRole = stackRole;
    }

    private void SetFinalLanding(FinalStackRole stackRole)
    {
        if (stackRole == FinalStackRole.Unknown)
            return;

        EnterFinalSequence();
        _finalStage = _landingCount == 0 ? FinalStage.Landing1 : FinalStage.Landing2;
        _currentFinalStackRole = stackRole;
        if (_firstFinalStackRole == FinalStackRole.Unknown)
            _firstFinalStackRole = stackRole;

        var ownStackRole = OwnFinalStackRole();
        if (ownStackRole == stackRole)
        {
            SetGuide(Center, TextOrEmpty(C.ShowFinalStackText, C.FinalStackText), GuidanceKind.FinalLanding,
                LandingCast, 0.0f, 0.0f);
            return;
        }

        if (TryGetOwnRolePosition(out var role))
        {
            // 1 番手 (T1/H1/M1/R1) が左、2 番手が右。ケフカ基準から 90 度ずつ振り分ける。
            // 塔を実際に観測できていれば、その角度にいちばん近い塔へ寄せる。
            var isLeft = role is RolePosition.T1 or RolePosition.H1 or RolePosition.M1 or RolePosition.R1;
            var angle = NormalizeAngle(FinalPairAnchorAngle() + (isLeft ? -MathF.PI / 2.0f : MathF.PI / 2.0f));
            var destination = _finalTowerPositions.Count == 0
                ? PositionFromDirectionAngle(angle, 10.0f)
                : _finalTowerPositions.OrderBy(position => AngleDistance(DirectionAngle(position), angle)).First();
            SetGuide(destination, TextOrEmpty(C.ShowFinalTowerText, C.FinalTowerText, RolePairName(role)),
                GuidanceKind.FinalLanding, LandingCast, 0.0f, 0.0f);
        }
        else
        {
            SetInstruction(TextOrEmpty(C.ShowFinalTowerText, C.FinalTowerText, "?"), GuidanceKind.FinalLanding);
        }
    }

    private void SetFinalRoleGuide(bool show, InternationalString text, GuidanceKind kind, uint actionId)
    {
        if (TryGetOwnRolePosition(out var role))
        {
            // ケフカ基準から見たロールごとの振り分け角。T/H が前 45 度、M/R が後ろ 135 度。
            var offset = role switch
            {
                RolePosition.T1 or RolePosition.H1 => -MathF.PI / 4.0f,
                RolePosition.T2 or RolePosition.H2 => MathF.PI / 4.0f,
                RolePosition.M1 or RolePosition.R1 => -3.0f * MathF.PI / 4.0f,
                RolePosition.M2 or RolePosition.R2 => 3.0f * MathF.PI / 4.0f,
                _ => 0.0f
            };
            var destination = PositionFromDirectionAngle(NormalizeAngle(FinalPairAnchorAngle() + offset), 9.8f);
            SetGuide(destination, TextOrEmpty(show, text, RolePairName(role)), kind, actionId, 0.0f, 0.0f);
        }
        else
        {
            SetInstruction(TextOrEmpty(show, text, "?"), kind);
        }
    }

    /// <summary>ギミック終了。表示を消して完了状態にする。自分の解決結果は残す。</summary>
    private void Complete()
    {
        _state = State.Completed;
        ClearMechanicState(clearSlot: false);
        HideElements();
    }

    #endregion

    #region private methods : reset
    /********************************************************************/
    /* private methods : reset                                          */
    /********************************************************************/
    // 状態の巻き戻し。ResetAll から下位の Clear が順に呼ばれる。
    // どのフィールドがどの Clear に属すかは private fields 区画のコメント参照。
    /// <summary>完全初期化。戦闘開始・終了・ディレクタ更新から呼ばれる。
    /// 下位の Clear をすべて経由するので、フィールドを足したらどれかに追加すること。</summary>
    private void ResetAll()
    {
        _groups.Clear();
        _selfPlayerId = 0;
        _sentMarkerCommand = false;
        _pendingMarkerCommand = "";
        _pendingTargetMarkerCommand = false;
        _markerCommandAtMs = 0;
        ClearMechanicState(clearSlot: true);
        _lastPacketCastSource = 0;
        _lastPacketCastId = 0;
        _lastPacketCastAtMs = 0;
        HideElements();
        _state = State.Idle;
    }

    private void ClearMechanicState(bool clearSlot)
    {
        _placedMasterMarkers = false;
        _earthPlayers.Clear();
        _accretionPlayers.Clear();
        _selfHadAccretionMarkerBlock = false;
        _kefkaId = 0;
        _kefkaPosition = null;
        _kefkaAnchorDebug = "";
        _earthMaxCount = 0;
        _currentWindow = -1;

        // 終盤シーケンス
        _finalStage = FinalStage.None;
        _finalInitialAnchorAngle = null;
        _finalInitialStackRole = FinalStackRole.Unknown;
        _finalInitialRoleOwner = 0;
        _firstFinalStackRole = FinalStackRole.Unknown;
        _secondFinalStackRole = FinalStackRole.Unknown;
        _currentFinalStackRole = FinalStackRole.Unknown;
        _landingCount = 0;
        _finalStackMarkerCount = 0;
        _finalDondokoHitCount = 0;
        _finalTowerPositions.Clear();

        if (clearSlot)
            ClearSelfResolution();
        else
            ClearSelfDisplayState();
        ClearGuide();
        ClearBlackHoleState();
    }

    private void ClearBlackHoleState()
    {
        ClearCurrentWindowTethers();
        _blackHolePositions.Clear();
        _hitSources.Clear();
        ClearFixedLaneSetBuckets();
    }

    #endregion

    #region private methods : display
    /********************************************************************/
    /* private methods : display                                        */
    /********************************************************************/
    // element の表示・非表示。ここまで来た時点で判定は終わっている。
    private void ShowDestination(Vector3 destination, string text, uint? color = null)
    {
        if (!Controller.TryGetElementByName(DestinationElement, out var element)) return;
        element.Enabled = true;
        element.color = color ?? GradientColor.Get(
            C.RainbowNavigationColor1.ToVector4(),
            C.RainbowNavigationColor2.ToVector4()).ToUint();
        element.SetRefPosition(destination);
        element.overlayText = text;
    }

    private bool IsSelfTetherTarget() => SelfTetherTarget != 0 && SelfTetherTarget == BasePlayer?.EntityId;

    private void ShowInstruction(string text)
    {
        if (!Controller.TryGetElementByName(InstructionElement, out var element)) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        element.Enabled = true;
        element.SetRefPosition(BasePlayer.Position);
        element.overlayText = text;
    }

    private void HideElements()
    {
        foreach (var element in Controller.GetRegisteredElements().Values)
            element.Enabled = false;
    }

    #endregion

    #region private methods : geometry / kefka anchor
    /********************************************************************/
    /* private methods : geometry / kefka anchor                        */
    /********************************************************************/
    private static bool TryResolveBlackHoleEndpoint(uint actorId, out Vector3 position, out int bucket)
    {
        if (actorId.GetObject() is { } obj && obj.BaseId == BlackHoleDataId && TryBucket(obj.Position, out bucket))
        {
            position = new Vector3(obj.Position.X, 0.0f, obj.Position.Z);
            return true;
        }

        position = default;
        bucket = -1;
        return false;
    }

    private static bool TryBucket(Vector3 position, out int bucket)
    {
        var v = new Vector2(position.X - Center.X, position.Z - Center.Z);
        var r = v.Length();
        // TryBucket: 中心からの距離がこの範囲にある object だけをブラックホールとみなす
        if (r is < 11.0f or > 23.0f)
        {
            bucket = -1;
            return false;
        }

        bucket = Math.Abs(v.X) > Math.Abs(v.Y)
            ? v.X > 0 ? 1 : 3
            : v.Y > 0 ? 2 : 0;
        return true;
    }

    /// <summary>ケフカのアンカー位置を更新する。詠唱のたびに呼ばれ、出所が 3 通りある。</summary>
    /// <remarks>優先順は「向きが読める詠唱から推定」→「フェーズ移行で実体を掴む」→「掴んだ実体を追う」。
    /// 推定した場合は <c>_kefkaId</c> を 0 に落とすので、以後は実体への追従が止まる。</remarks>
    private void UpdateKefkaAnchor(uint actorId, uint actionId, float? eventRotation = null)
    {
        // ケフカの向きから見て、この詠唱がどちらを向いて出るかの相対角。
        // NaN はアンカーを読めない詠唱、つまりこの経路では位置を決めないという意味。
        var offset = actionId switch
        {
            47846 => MathF.PI / 2.0f,    // ビンタ頭割り  -> +90 度
            47847 => -MathF.PI / 2.0f,   // ビンタ散開    -> -90 度
            47852 => 0.0f,               // アズイズ 1 回目 -> 正面
            47853 => MathF.PI,           // アズイズ 2 回目 -> 背面
            _ => float.NaN
        };

        // 向きはパケットにあればそれ、無ければ詠唱者の現在の向き。
        var rotation = 0.0f;
        var hasRotation = false;
        if (eventRotation.HasValue)
        {
            rotation = eventRotation.Value;
            hasRotation = true;
        }
        else if (actorId.GetObject() is { } caster)
        {
            rotation = caster.Rotation;
            hasRotation = true;
        }

        if (!float.IsNaN(offset) && hasRotation)
        {
            // まず回転角がいちばん近いアンカー候補を探す。許容差 45 度は広く、
            // ID は確定しない (_kefkaId は 0 のまま)。観測ではなく推定。
            IGameObject? best = null;
            var bestDiff = float.MaxValue;
            foreach (var obj in Svc.Objects)
            {
                if (!IsKefkaAnchorObject(obj))
                    continue;
                var diff = AngleDistance(obj.Rotation, rotation);
                if (diff < bestDiff)
                {
                    best = obj;
                    bestDiff = diff;
                }
            }

            // 45 度は広い。近いクローンを取り違える余地がある。
            if (best != null && bestDiff <= MathF.PI / 4.0f)
            {
                _kefkaId = 0;
                _kefkaPosition = FlatPosition(best.Position);
                _kefkaAnchorDebug = $"rotation-match {Deg(rotation):F1}->{Describe(best.EntityId)}";
                return;
            }

            // 候補が無ければ、角度だけから仮の位置を作る。半径 20 は盤外寄りの当て値。
            var deg = Deg(offset);
            var signed = deg switch
            {
                > 0.0f => $"+{deg:F1}",
                < 0.0f => $"{deg:F1}",
                _ => "+0.0"
            };
            _kefkaId = 0;
            _kefkaPosition = PositionFromDirectionAngle(NormalizeAngle(rotation + offset), 20.0f);
            _kefkaAnchorDebug = $"cast-rotation {Deg(rotation):F1}{signed}";
        }
        else if (actionId == DecisiveBattleChaos)
            CaptureKefka(actorId, true);
        else if (actorId != 0 && actorId == _kefkaId)
            CaptureKefka(actorId, false);
    }

    private void RefreshKefkaAnchorFromObject()
    {
        if (_kefkaId != 0)
            CaptureKefka(_kefkaId, false);
    }

    private void CaptureKefka(uint actorId, bool force)
    {
        if (actorId.GetObject() is { } obj)
            CaptureKefka(obj, force);
    }

    private void ObserveFinalTowerSource(IGameObject? source, Vector3? eventPosition, uint actionId, string origin)
    {
        if (actionId is not (DondokoHit or TowerImpact))
            return;

        var position = source != null ? FlatPosition(source.Position) :
            eventPosition.HasValue ? FlatPosition(eventPosition.Value) : default;
        var distance = Vector2.Distance(new Vector2(position.X, position.Z), new Vector2(Center.X, Center.Z));
        if (distance is < 7.5f or > 13.5f ||
            _finalTowerPositions.Any(x => Vector3.DistanceSquared(x, position) < 1.0f))
            return;

        _finalTowerPositions.Add(position);
        if (_guideKind == GuidanceKind.FinalLanding &&
            _currentFinalStackRole != FinalStackRole.Unknown &&
            TryGetOwnRolePosition(out _) &&
            OwnFinalStackRole() != _currentFinalStackRole)
            SetFinalLanding(_currentFinalStackRole);
    }

    private void CaptureKefka(IGameObject? obj, bool force)
    {
        if (obj == null) return;
        if (!force && _kefkaId != 0 && obj.EntityId != _kefkaId) return;
        var position = FlatPosition(obj.Position);
        if (!IsKefkaAnchorPosition(position)) return;
        _kefkaId = obj.EntityId;
        _kefkaPosition = position;
        _kefkaAnchorDebug = $"object {Describe(obj.EntityId)}";
    }

    private bool TryGetKefkaPosition(out Vector3 position)
    {
        if (_kefkaPosition is { } cached)
        {
            position = cached;
            return true;
        }

        if (_kefkaId.GetObject() is { } obj)
        {
            position = FlatPosition(obj.Position);
            return true;
        }

        position = default;
        return false;
    }

    private static bool IsKefkaAnchorPosition(Vector3 position) =>
        // IsKefkaAnchorPosition: 中心からこれ以上離れていればアンカーとみなす
        Vector2.Distance(new Vector2(position.X, position.Z), new Vector2(Center.X, Center.Z)) >= 5.0f;

    private static bool IsKefkaAnchorObject(IGameObject obj) =>
        obj.BaseId == 19451 && IsKefkaAnchorPosition(FlatPosition(obj.Position));

    private float KefkaAnchorAngle() =>
        TryGetKefkaPosition(out var kefka) ? DirectionAngle(kefka) : 0.0f;

    private string KefkaAnchorDebugText() => TryGetKefkaPosition(out var pos) ? KefkaAnchorDebugText(pos) : "Kefka missing -> N";

    private string KefkaAnchorDebugText(Vector3 pos) =>
        $"{(string.IsNullOrWhiteSpace(_kefkaAnchorDebug) ? "Kefka" : _kefkaAnchorDebug)}({pos.X:F1},{pos.Z:F1})";

    private static Vector3 FlatPosition(Vector3 position) => new(position.X, 0.0f, position.Z);

    private static string DescribeTethers(ICharacter character)
    {
        var entries = new List<string>();
        var tethers = character.Struct()->Vfx.Tethers;
        for (var i = 0; i < tethers.Length; i++)
        {
            var tether = tethers[i];
            if (tether.Id == 0) continue;
            entries.Add($"{i}:{tether.Id}/{tether.Progress}/{tether.TargetId.ObjectId:X8}");
        }

        return entries.Count == 0 ? "none" : string.Join(",", entries);
    }

    private static float DirectionAngle(Vector3 position)
    {
        var v = new Vector2(position.X - Center.X, Center.Z - position.Z);
        return v.LengthSquared() < 0.01f ? 0.0f : NormalizeAngle(MathF.Atan2(v.X, v.Y));
    }

    private static float NormalizeAngle(float angle)
    {
        const float tau = MathF.PI * 2.0f;
        angle %= tau;
        return angle < 0.0f ? angle + tau : angle;
    }

    private static float AngleDistance(float a, float b)
    {
        var diff = Math.Abs(NormalizeAngle(a) - NormalizeAngle(b));
        return Math.Min(diff, MathF.PI * 2.0f - diff);
    }

    #endregion

    #region private methods : bucket expectation
    /********************************************************************/
    /* private methods : bucket expectation                             */
    /********************************************************************/
    // 「自分はどの bucket を取るべきか」の期待値算出。割り当てモードごとに経路が分かれる。
    // ウィンドウ 0,1,8,9 は snake、2〜7 は marker flex という別扱いがある。
    private LineBaitDirection LaneBaitDirection(int lane)
    {
        if (lane == 2) return C.AccretionLineBaitDirection;
        if (lane is not (0 or 1)) return C.LineBaitDirection;

        var supportFirst = C.FirstOrbRole == FirstOrbRole.Support;
        var supportLane = lane == 0 ? supportFirst : !supportFirst;
        return supportLane ? C.SupportLineBaitDirection : C.DpsLineBaitDirection;
    }

    private int ExpectedBucket(Slot slot)
    {
        if (TryFirstPairBucket(slot, out var firstPairBucket))
            return firstPairBucket;

        var rank = ExpectedRank(slot, _currentWindow);

        // FixedRoleAccretion: 順位そのものがレーン。セット先頭で焼き付けた bucket を使う。
        if (C.AssignmentMode == AssignmentMode.FixedRoleAccretion)
        {
            if (rank < 0) return -1;
            var startWindow = FixedMarkerSetStartWindow(_currentWindow);
            if (startWindow < 0 || rank >= _fixedLaneSetBuckets.Length)
                return ExpectedFixedSpotBucketUncached(rank);

            if (_fixedLaneSetStartWindow != startWindow)
            {
                _fixedLaneSetStartWindow = startWindow;
                Array.Fill(_fixedLaneSetBuckets, -1);
            }

            if (_fixedLaneSetBuckets[rank] < 0 &&
                _currentWindow == startWindow &&
                _tetherTargets.Count >= ExpectedSourcesByWindow[startWindow])
            {
                var fixedBucket = ExpectedFixedSpotBucketUncached(rank);
                if (fixedBucket >= 0)
                    _fixedLaneSetBuckets[rank] = fixedBucket;
            }

            return _fixedLaneSetBuckets[rank];
        }

        // FixedMarkerLanes: レーンはスロットから引く。蛇行ウィンドウだけはマーカー位置から
        // 順に舐めて、線が生きている最初の bucket を取る。
        if (C.AssignmentMode == AssignmentMode.FixedMarkerLanes)
        {
            if (rank < 0) return -1;
            var lane = RankFromSlot(slot);
            if (lane < 0) return -1;

            if (IsSnakeSetWindow(_currentWindow) && lane is 0 or 1)
            {
                var scanBuckets = OrderedBuckets();
                var start = (int)LaneMarker(lane);
                if (start < 0 || start >= scanBuckets.Count)
                    return -1;

                var step = DirectionStep(LaneBaitDirection(lane));
                for (var i = 0; i < scanBuckets.Count; i++)
                {
                    var candidate = scanBuckets[(start + step * i + scanBuckets.Count) % scanBuckets.Count];
                    if (_tetherTargets.ContainsKey(candidate))
                        return candidate;
                }
                return -1;
            }

            var laneStartWindow = FixedMarkerSetStartWindow(_currentWindow);
            if (laneStartWindow < 0 || lane > 2)
                return -1;

            if (_fixedLaneSetStartWindow != laneStartWindow)
            {
                _fixedLaneSetStartWindow = laneStartWindow;
                Array.Fill(_fixedLaneSetBuckets, -1);
            }

            if (_currentWindow == laneStartWindow &&
                _tetherTargets.Count >= ExpectedSourcesByWindow[laneStartWindow] &&
                _fixedLaneSetBuckets[lane] < 0)
            {
                var laneBucket = ExpectedMarkerOrFlexLaneBucket(lane);
                if (laneBucket >= 0)
                    _fixedLaneSetBuckets[lane] = laneBucket;
            }

            return _fixedLaneSetBuckets[lane];
        }

        var buckets = OrderedActiveBuckets();
        return rank >= 0 && rank < buckets.Count ? buckets[rank] : -1;
    }

    /// <summary>2 本目の窓 (window 1) の距離判定。決めるのは <see cref="FreezeWindowDecisions"/> で、
    /// ここは確定した組を返すだけ。</summary>
    /// <remarks>戻り値 true は「この窓この slot は距離判定の担当」の意味で、未確定なら bucket は -1。
    /// -1 は期待なし = 待機表示になる。確定前に角度順の仮の答えを出すと、2 本目の線が出た瞬間に
    /// 距離の答えへ切り替わって誘導が飛ぶので、決まるまで出さない。</remarks>
    private bool TryFirstPairBucket(Slot slot, out int bucket)
    {
        bucket = -1;
        if (C.FirstPairAssignment != FirstPairAssignment.FirstSlotNearest || _currentWindow != 1 ||
            slot is not (Slot.Attack1 or Slot.Attack2))
            return false;

        if (_windowFirstPair is { } pair)
            bucket = slot == Slot.Attack1 ? pair.First : pair.Second;
        return true;
    }

    private int ExpectedFixedSpotBucketUncached(int rank)
    {
        if (rank < 0) return -1;
        var buckets = OrderedBuckets();
        if (rank >= buckets.Count) return -1;
        var preferred = buckets[rank];
        if (_tetherTargets.ContainsKey(preferred))
            return preferred;
        var fallback = buckets[(int)C.FallbackMarker];
        return _tetherTargets.ContainsKey(fallback) ? fallback : -1;
    }

    private int ExpectedMarkerOrFlexLaneBucket(int lane)
    {
        if (TryMarkerBucket(LaneMarker(lane), out var bucket))
            return bucket;

        if (!IsMarkerFlexSetWindow(_currentWindow))
            return -1;

        return TryMarkerBucket(C.LaneFlexMarker, out bucket) ? bucket : -1;
    }

    private void ClearFixedLaneSetBuckets()
    {
        _fixedLaneSetStartWindow = -1;
        Array.Fill(_fixedLaneSetBuckets, -1);
    }

    private MapMarker LaneMarker(int lane)
    {
        if (lane == 2) return C.LaneAccretionMarker;

        var supportFirst = C.FirstOrbRole == FirstOrbRole.Support;
        var supportLane = lane == 0 ? supportFirst : !supportFirst;
        return supportLane ? C.LaneSupportMarker : C.LaneDpsMarker;
    }

    private bool TryMarkerBucket(MapMarker marker, out int bucket)
    {
        var buckets = OrderedBuckets();
        bucket = (int)marker < buckets.Count ? buckets[(int)marker] : -1;
        return bucket >= 0 && _tetherTargets.ContainsKey(bucket);
    }

    #endregion

    #region private methods : debug text
    /********************************************************************/
    /* private methods : debug text                                     */
    /********************************************************************/
    // Debug 表示用の文字列生成のみ。判定には影響しない。
    private string BlackHoleExpectedDebugText(string reason)
    {
        var expected = ExpectedBucket(_selfSlot);
        var expectedTarget = expected >= 0 && _tetherTargets.TryGetValue(expected, out var target)
            ? Describe(target)
            : "none";
        var expectedSource = expected >= 0 && _tetherSources.ContainsKey(expected);
        var selfTarget = SelfTetherTarget != 0 && SelfTetherTarget == BasePlayer?.EntityId;

        // 着弾済み bucket。分母はそのウィンドウで来るはずの本数。
        var expectedSources = _currentWindow >= 0 && _currentWindow < ExpectedSourcesByWindow.Length
            ? ExpectedSourcesByWindow[_currentWindow]
            : 0;
        var hitText = _hitSources.Count == 0
            ? "none"
            : string.Join(",", _hitSources.OrderBy(x => x).Select(DirectionName));
        var hits = $"{_hitSources.Count}/{expectedSources}[{hitText}]";

        // A/B/C/D のレーン割り当てと、いま線が生きている bucket。
        var ordered = string.Join(", ", OrderedBuckets()
            .Select((bucket, index) => $"{(MapMarker)index}={DirectionName(bucket)}"));
        var active = string.Join(", ", _tetherTargets
            .OrderBy(x => x.Key)
            .Select(x => $"{DirectionName(x.Key)}->{Describe(x.Value)}"));

        return $"reason={reason} mode={C.AssignmentMode} window={_currentWindow} slot={_selfSlot} " +
               $"rank={ExpectedRank(_selfSlot, _currentWindow)} expected={DirectionName(expected)} " +
               // liveActors の分母 12 はデバッグ表示のみ。実際の判定には使っていない
               $"hits={hits} liveActors={_blackHolePositions.Count}/12 " +
               $"source={expectedSource} target={expectedTarget} selfBucket={DirectionName(SelfTetherBucket)} " +
               $"selfTarget={Describe(SelfTetherTarget)} targetSelf={selfTarget} ordered=[{ordered}] " +
               $"active=[{active}] decision={DecisionText()}";

        // どの規則で bucket を決めたのかを 1 行にする。判定には影響しない。
        string DecisionText()
        {
            var rank = ExpectedRank(_selfSlot, _currentWindow);
            if (_selfSlot == Slot.None)
                return "slot=none";
            if (rank < 0)
                return "not-assigned-this-window";
            if (TryFirstPairBucket(_selfSlot, out var firstPairBucket))
                return firstPairBucket < 0
                    ? "first-pair waiting (2 lines + Attack1)"
                    : $"first-pair nearest expected={DirectionName(firstPairBucket)}";
            if (C.AssignmentMode == AssignmentMode.FixedMarkerLanes)
                return FixedMarkerLaneDecisionText(_selfSlot, rank);
            if (C.AssignmentMode == AssignmentMode.FixedRoleAccretion)
            {
                var cached = rank is >= 0 and <= 2 ? _fixedLaneSetBuckets[rank] : -1;
                var markerText = MarkerBucketText((MapMarker)Math.Clamp(rank, 0, MapMarkerNames.Length - 1));
                if (!IsMarkerFlexSetWindow(_currentWindow))
                    return $"fixed-role rank={rank} markerIndex={rank} marker={markerText} no-cache-window";
                return $"fixed-role rank={rank} markerIndex={rank} set={FixedMarkerSetStartWindow(_currentWindow)} " +
                       $"cached={DirectionName(cached)} marker={markerText} fallback={MarkerBucketText(C.FallbackMarker)}";
            }

            return $"ordered-active rank={rank} count={OrderedActiveBuckets().Count}";
        }
    }

    private string FixedMarkerLaneDecisionText(Slot slot, int rank)
    {
        var lane = RankFromSlot(slot);
        if (lane < 0)
            return "fixed-lane lane=none";

        var marker = LaneMarker(lane);

        // レーン 0/1 は FirstOrbRole 次第で Support/DPS が入れ替わる。2 は常に Accretion。
        var laneName = lane == 2
            ? "Accretion"
            : lane is not (0 or 1)
                ? "Unknown"
                : (lane == 0) == (C.FirstOrbRole == FirstOrbRole.Support) ? "Support" : "DPS";

        if (IsSnakeSetWindow(_currentWindow) && lane is 0 or 1)
        {
            // 蛇行ウィンドウはマーカー位置から順に舐めて探す。その走査順をそのまま出す。
            var direction = LaneBaitDirection(lane);
            var buckets = OrderedBuckets();
            var start = (int)marker;
            string scan;
            if (start < 0 || start >= buckets.Count)
            {
                scan = "invalid-marker";
            }
            else
            {
                var step = DirectionStep(direction);
                var parts = new List<string>();
                for (var i = 0; i < buckets.Count; i++)
                {
                    var index = (start + step * i + buckets.Count) % buckets.Count;
                    var bucket = buckets[index];
                    parts.Add($"{(MapMarker)index}->{DirectionName(bucket)}:" +
                              $"{(_tetherTargets.TryGetValue(bucket, out var target) ? Describe(target) : "none")}");
                }
                scan = string.Join(" ", parts);
            }

            return $"fixed-lane rank={rank} lane={lane}:{laneName} snake marker={marker} " +
                   $"dir={direction} scan=[{scan}]";
        }

        var cached = lane is >= 0 and <= 2 ? _fixedLaneSetBuckets[lane] : -1;
        var markerText = MarkerBucketText(marker);
        if (!IsMarkerFlexSetWindow(_currentWindow))
            return $"fixed-lane rank={rank} lane={lane}:{laneName} marker={markerText} no-flex-window";

        return $"fixed-lane rank={rank} lane={lane}:{laneName} set={FixedMarkerSetStartWindow(_currentWindow)} " +
               $"cached={DirectionName(cached)} marker={markerText} flex={MarkerBucketText(C.LaneFlexMarker)}";
    }

    private string MarkerBucketText(MapMarker marker)
    {
        var buckets = OrderedBuckets();
        var index = (int)marker;
        if (index < 0 || index >= buckets.Count)
            return $"{marker}->invalid";

        var bucket = buckets[index];
        return $"{marker}->{DirectionName(bucket)}:{(_tetherTargets.TryGetValue(bucket, out var target) ? Describe(target) : "none")}";
    }

    #endregion

    #region private methods : helpers
    /********************************************************************/
    /* private methods : helpers                                        */
    /********************************************************************/
    // 純粋関数と小さな変換。状態を持たない。
    private static int ExpectedRank(Slot slot, int window) =>
        window >= 0 && window < BlackHoleWindowSlots.Length
            ? Array.IndexOf(BlackHoleWindowSlots[window], slot)
            : -1;

    private static bool IsSnakeSetWindow(int window) => window is 0 or 1 or 8 or 9;

    private static bool IsMarkerFlexSetWindow(int window) => window is >= 2 and <= 7;

    private static int FixedMarkerSetStartWindow(int window) => window switch
    {
        >= 2 and <= 4 => 2,
        >= 5 and <= 7 => 5,
        _ => -1
    };

    private int DirectionStep(LineBaitDirection direction)
    {
        var orderedClockwise = C.BlackHoleSourceOrder == BlackHoleSourceOrder.ClockwiseFromNorth;
        return (direction == LineBaitDirection.Clockwise) == orderedClockwise ? 1 : -1;
    }

    private List<int> OrderedActiveBuckets()
    {
        return OrderedBuckets().Where(_tetherTargets.ContainsKey).ToList();
    }

    private List<int> OrderedBuckets()
    {
        // 起点は窓ごとに凍結した巨大ケフカの方角 (FreezeWindowDecisions)。
        // 線が出そろう前は今の位置、掴めていなければ北。設定が盤面北なら常に北。
        var anchor = _windowOrderAnchorAngle ?? (C.BlackHoleOrderAnchor switch
        {
            BlackHoleOrderAnchor.KefkaPosition when TryGetKefkaPosition(out var kefka) => DirectionAngle(kefka),
            _ => 0.0f
        });
        return Enumerable.Range(0, 4)
            .OrderBy(bucket =>
            {
                // 線の始点が分かっていればその向き、分かっていなければ bucket の代表角 (北から 90 度刻み)。
                var sourceAngle = _tetherSources.TryGetValue(bucket, out var source)
                    ? DirectionAngle(source)
                    : NormalizeAngle(bucket * MathF.PI / 2.0f);
                var delta = C.BlackHoleSourceOrder == BlackHoleSourceOrder.ClockwiseFromNorth
                    ? NormalizeAngle(sourceAngle - anchor)
                    : NormalizeAngle(anchor - sourceAngle);
                // 起点とほぼ同じ向き (5 度以内) は 0 に丸め、番号順の tie-break に任せる。
                return Math.Min(delta, MathF.PI * 2.0f - delta) <= MathF.PI / 36.0f ? 0.0f : delta;
            })
            .ThenBy(bucket => bucket)
            .ToList();
    }

    private void SetInstruction(string text, GuidanceKind kind)
    {
        _guideDestination = null;
        _guideActionId = 0;
        _guideText = "";
        _guideInstruction = text;
        _guideKind = kind;
        _guideDebug = kind.ToString();
    }

    private void SetGuide(Vector3 destination, string text, GuidanceKind kind, uint actionId, float rotation, float offset)
    {
        _guideDestination = destination;
        _guideActionId = actionId;
        _guideText = text;
        _guideInstruction = "";
        _guideKind = kind;
        _guideDebug = $"action={actionId} rot={Deg(rotation):F1} off={Deg(offset):F1} ref=({destination.X:F2},{destination.Z:F2})";
    }

    private void ClearGuide()
    {
        _guideDestination = null;
        _guideActionId = 0;
        _guideText = "";
        _guideInstruction = "";
        _guideDebug = "";
        if (_guideKind != GuidanceKind.None)
            _guideKind = GuidanceKind.None;
    }

    private bool TryFinalStackRole(uint actorId, out FinalStackRole role)
    {
        if (actorId.GetObject() is IPlayerCharacter pc)
        {
            role = StackRoleFromCombat(pc.GetRole());
            return role != FinalStackRole.Unknown;
        }

        if (TryGetConfiguredRole(actorId, out var position))
        {
            role = StackRoleFromRolePosition(position);
            return role != FinalStackRole.Unknown;
        }

        role = FinalStackRole.Unknown;
        return false;
    }

    private FinalStackRole OwnFinalStackRole()
    {
        if (TryGetOwnRolePosition(out var role))
            return StackRoleFromRolePosition(role);
        return BasePlayer == null ? FinalStackRole.Unknown : StackRoleFromCombat(BasePlayer.GetRole());
    }

    private bool TryGetOwnRolePosition(out RolePosition role)
    {
        if (BasePlayer != null && TryGetConfiguredRole(BasePlayer.EntityId, out role))
            return true;

        role = RolePosition.Not_Selected;
        return false;
    }

    private bool TryGetConfiguredRole(uint actorId, out RolePosition role)
    {
        var list = C.PriorityData.GetFirstValidList();
        if (list != null)
            foreach (var entry in list.List)
                if (entry.IsInParty(list.IsRole, out var member) && member.IGameObject.EntityId == actorId &&
                    entry.Role != RolePosition.Not_Selected)
                {
                    role = entry.Role;
                    return true;
                }

        role = RolePosition.Not_Selected;
        return false;
    }

    private static FinalStackRole StackRoleFromCombat(CombatRole role) =>
        role == CombatRole.DPS ? FinalStackRole.Dps :
        role is CombatRole.Tank or CombatRole.Healer ? FinalStackRole.Support : FinalStackRole.Unknown;

    private static FinalStackRole StackRoleFromRolePosition(RolePosition role) => role switch
    {
        RolePosition.T1 or RolePosition.T2 or RolePosition.H1 or RolePosition.H2 => FinalStackRole.Support,
        RolePosition.M1 or RolePosition.M2 or RolePosition.R1 or RolePosition.R2 => FinalStackRole.Dps,
        _ => FinalStackRole.Unknown
    };

    private float FinalPairAnchorAngle() => KefkaAnchorAngle();

    private static string RolePairName(RolePosition role) => role switch
    {
        RolePosition.T1 or RolePosition.H1 => "MT/H1",
        RolePosition.T2 or RolePosition.H2 => "ST/H2",
        RolePosition.M1 or RolePosition.R1 => "D1/D3",
        RolePosition.M2 or RolePosition.R2 => "D2/D4",
        _ => "?"
    };

    private static Vector3 RadialFromFacing(float rotation, float offset, float radius)
    {
        var angle = rotation + offset;
        return Center + new Vector3(MathF.Cos(angle) * radius, 0.0f, MathF.Sin(angle) * radius);
    }

    private static Vector3 PositionFromDirectionAngle(float angle, float radius) =>
        Center + new Vector3(MathF.Sin(angle) * radius, 0.0f, -MathF.Cos(angle) * radius);

    private static float Deg(float radians) => radians * 180.0f / MathF.PI;

    private static Vector4 WithDefaultAlpha(Vector4 color) => color with { W = 200.0f / 255.0f };

    private static TargetGroup? GroupFromStatus(uint statusId) => statusId switch
    {
        3004 => TargetGroup.Attack,
        3005 => TargetGroup.Bind,
        3006 => TargetGroup.Stop,
        _ => null
    };

    private static Slot SlotFromRank(TargetGroup group, int rank) => (group, rank) switch
    {
        (TargetGroup.Attack, 0) => Slot.Attack1,
        (TargetGroup.Attack, 1) => Slot.Attack2,
        (TargetGroup.Attack, 2) => Slot.Attack3,
        (TargetGroup.Bind, 0) => Slot.Bind1,
        (TargetGroup.Bind, 1) => Slot.Bind2,
        (TargetGroup.Bind, 2) => Slot.Bind3,
        (TargetGroup.Stop, 0) => Slot.Stop1,
        (TargetGroup.Stop, 1) => Slot.Stop2,
        _ => Slot.None
    };

    private static int RankFromSlot(Slot slot) => slot switch
    {
        Slot.Attack1 or Slot.Bind1 or Slot.Stop1 => 0,
        Slot.Attack2 or Slot.Bind2 or Slot.Stop2 => 1,
        Slot.Attack3 or Slot.Bind3 => 2,
        _ => -1
    };

    private static string SlotName(Slot slot) => slot switch
    {
        Slot.Attack1 => "First1",
        Slot.Attack2 => "First2",
        Slot.Attack3 => "First3",
        Slot.Bind1 => "Second1",
        Slot.Bind2 => "Second2",
        Slot.Bind3 => "Second3",
        Slot.Stop1 => "Third1",
        Slot.Stop2 => "Third2",
        _ => "?"
    };

    private static string DirectionName(int bucket) => bucket switch
    {
        0 => "N",
        1 => "E",
        2 => "S",
        3 => "W",
        _ => "?"
    };

    private static string Format(InternationalString text, params object[] args)
    {
        try { return string.Format(text.Get(), args); }
        catch { return text.Get(); }
    }

    private static string TextOrEmpty(bool show, InternationalString text, params object[] args)
    {
        if (!show) return "";
        return args.Length == 0 ? text.Get() : Format(text, args);
    }

    private static string Describe(uint actorId)
    {
        if (actorId == 0)
            return "none";
        if (actorId.GetObject() is { } obj)
            return $"{obj.Name}(0x{actorId:X8})@({obj.Position.X:F1},{obj.Position.Z:F1})";
        return $"0x{actorId:X8}";
    }

    private static bool DrawCombo(string label, ref int selected, string[] items, float width)
    {
        ImGui.SetNextItemWidth(width);
        return ImGui.Combo(label, ref selected, items, items.Length);
    }

    private static AssignmentMode NormalizeAssignmentMode(AssignmentMode mode) =>
        AssignmentModeValues.Contains(mode) ? mode : AssignmentMode.PartyMarker;

    private static void DrawFloat(string label, ref float value)
    {
        ImGui.SetNextItemWidth(120f);
        ImGui.InputFloat(label, ref value, 0.05f, 0.5f, "%.2f");
    }

    private static void DrawSubsection(string label)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(label);
    }

    private static void DrawCommand(string label, ref string command)
    {
        command ??= "";
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(label, ref command, 160);
    }

    private static void DrawText(string label, InternationalString text, ref bool show)
    {
        ImGui.PushID(label);
        ImGui.Checkbox("Show", ref show);
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1f);
        var value = text.Get();
        text.ImGuiEdit(ref value);
        ImGui.Spacing();
        ImGui.PopID();
    }

    private static void DrawColor(string label, ref uint color)
    {
        var value = color.ToVector4();
        ImGui.SetNextItemWidth(220f);
        if (ImGui.ColorEdit4(label, ref value, ImGuiColorEditFlags.NoInputs))
            color = value.ToUint();
    }

    private static PriorityData CreatePriorityData(string name, string description, IReadOnlyList<RolePosition> roles) => new()
    {
        Name = name,
        Description = description,
        PriorityLists =
        [
            new PriorityList
            {
                IsRole = true,
                List = roles.Select(role => new JobbedPlayer { Role = role }).ToList()
            }
        ]
    };

    #endregion

    #region types
    /********************************************************************/
    /* types                                                            */
    /********************************************************************/
    // 入れ子型。State 系 enum、設定用の公開 enum、Config。
    // Config は 62 項目あり、正しさが 8 人の設定一致に依存する点に注意。
    private readonly record struct BlackHoleTask(int Bucket, Vector3 Source, uint Target, Vector3 StandPosition);

    private enum State { Idle, CollectingAssignments, BlackHoleActive, FinalSequence, Completed }
    public enum AssignmentMode
    {
        PartyMarker = 0,
        Priority = 1,
        MarkerThenPriority = 2,
        RoleAccretion = 3,
        FixedRoleAccretion = 4,
        FixedMarkerLanes = 5
    }
    private enum AssignmentQuality { Unknown, Marker, Priority, RoleAccretion }
    private enum GuidanceKind { None, FinalCenter, FinalSpread, FinalLanding, FinalMove }
    private enum FinalStage { None, AwaitingBlizzaga, CenterBait, RoleSpread, Landing1, Landing2, ProtrudeMove }
    private enum FinalStackRole { Unknown, Support, Dps }
    private enum TargetGroup { None, Attack, Bind, Stop }
    private enum Slot { None, Attack1, Attack2, Attack3, Bind1, Bind2, Bind3, Stop1, Stop2 }
    public enum LineBaitDirection { Clockwise, Counterclockwise }
    public enum FirstWindowBaitDirection { SameAsLineBaitDirection = 0, Clockwise = 1, Counterclockwise = 2 }
    public enum FirstPairAssignment { SourceOrder = 0, FirstSlotNearest = 1 }
    public enum BlackHoleSourceOrder { ClockwiseFromNorth = 0, CounterclockwiseFromNorth = 1 }
    public enum BlackHoleOrderAnchor { KefkaPosition = 0, ArenaNorth = 1 }
    public enum FirstOrbRole { Dps = 0, Support = 1 }
    public enum FinalInitialBaitMode { Center = 0, KefkaRelativeRoleSplit = 1 }
    public enum FinalInitialNorthRole { Support = 0, Dps = 1 }
    public enum MapMarker { A = 0, B = 1, C = 2, D = 3 }
    public enum MarkerCommandSource { TargetDebuff = 0, AccretionDebuff = 1 }
    // Unset は旧設定 (ExecuteMarkerCommand / IsMaster) からの移行待ちを表す。
    // EnsureDefaults が最初の 1 回で潰すので、UI と判定には出てこない。
    public enum MarkerPlacement { Unset = -1, Off = 0, EachPlayer = 1, Master = 2 }

    public sealed class Config
    {
        public AssignmentMode AssignmentMode = AssignmentMode.PartyMarker;
        public FirstOrbRole FirstOrbRole = FirstOrbRole.Dps;
        public LineBaitDirection LineBaitDirection = LineBaitDirection.Clockwise;
        public FirstWindowBaitDirection FirstWindowBaitDirection = FirstWindowBaitDirection.SameAsLineBaitDirection;
        public FirstPairAssignment FirstPairAssignment = FirstPairAssignment.SourceOrder;
        public BlackHoleSourceOrder BlackHoleSourceOrder = BlackHoleSourceOrder.ClockwiseFromNorth;
        public BlackHoleOrderAnchor BlackHoleOrderAnchor = BlackHoleOrderAnchor.KefkaPosition;
        public FinalInitialBaitMode FinalInitialBaitMode = FinalInitialBaitMode.Center;
        public FinalInitialNorthRole FinalInitialNorthRole = FinalInitialNorthRole.Support;
        public bool BlackHoleTetherOnly;
        public bool ShowPostBlackHoleNavigation = true;
        public uint RainbowNavigationColor1 = WithDefaultAlpha(EColor.CyanBright).ToUint();
        public uint RainbowNavigationColor2 = WithDefaultAlpha(EColor.VioletBright).ToUint();
        public uint CorrectTetherColor = WithDefaultAlpha(EColor.GreenBright).ToUint();
        public uint WrongTetherColor = WithDefaultAlpha(EColor.RedBright).ToUint();
        public uint UnknownTetherColor = WithDefaultAlpha(EColor.YellowBright).ToUint();
        public int[] MarkerLineOrders = [0, 1, 2, 0, 1, 2, 0, 1];
        // 誰が /mk を撃つか。Off / 各自 / マスター 1 人の 3 択で、同時には成立しない。
        public MarkerPlacement MarkerPlacement = MarkerPlacement.Unset;
        // 旧設定。独立した 2 つのフラグで両方 ON にでき、番号の奪い合いが起きていた。
        // 読むのは EnsureDefaults の移行だけ。UI には出さない。
        public bool ExecuteMarkerCommand;
        public MarkerCommandSource MarkerCommandSource = MarkerCommandSource.TargetDebuff;
        public bool SkipTargetMarkerOnAccretion;
        public float MarkerDelayMinSeconds = 0.1f;
        public float MarkerDelayMaxSeconds = 0.8f;
        public string FirstTargetCommand = "/mk attack <me>";
        public string SecondTargetCommand = "/mk bind <me>";
        public string ThirdTargetCommand = "/mk stop <me>";
        public string AccretionCommand = "/mk bind <me>";
        // マスターが優先順位リストから 8 人分の頭上マーカーを置く。PartyMarker モードの人が
        // 期待する配置を、誰もマーカー待ちで判断を保留せずに再現するため。
        // パーティ内でちょうど 1 人だけが有効にすること。
        public bool IsMaster;
        public bool IsMasterClearFirst = true;
        public string IsMasterClearCommand = "/mk off <{0}>";
        public PriorityData PriorityData = CreatePriorityData("P3 Earthquake priority",
            "Used when assignment mode is Priority.", DefaultRolePriority);

        public InternationalString FirstLineWindowText = new() { En = "W{0}: take line at W{1}", Jp = "W{0}: W{1}で線取り" };
        public bool ShowFirstLineWindowText = true;
        public InternationalString NextLineWindowText = new() { En = "W{0}: take next line", Jp = "W{0}: 次線を取る" };
        public bool ShowNextLineWindowText = true;
        public InternationalString TakeLineNowText = new() { En = "W{0}: take line", Jp = "W{0}: 線を取れ" };
        public bool ShowTakeLineNowText = true;
        public InternationalString UnknownSlotText = new() { En = "Earthquake slot unknown", Jp = "地震スロット未確定" };
        public bool ShowUnknownSlotText = true;
        public InternationalString OverlayText = new() { En = "{0}", Jp = "{0}" };
        public bool ShowOverlayText = true;
        public InternationalString FinalCenterText = new() { En = "Center bait", Jp = "中央で誘導" };
        public bool ShowFinalCenterText = true;
        public InternationalString FinalRoleSplitText = new() { En = "{0}: bait", Jp = "{0}: 誘導" };
        public bool ShowFinalRoleSplitText = true;
        public InternationalString FinalSpreadText = new() { En = "{0}: spread", Jp = "{0}: 散開" };
        public bool ShowFinalSpreadText = true;
        public InternationalString FinalStackText = new() { En = "Stack center", Jp = "中央で頭割り" };
        public bool ShowFinalStackText = true;
        public InternationalString FinalTowerText = new() { En = "{0}: tower", Jp = "{0}: 塔" };
        public bool ShowFinalTowerText = true;
        public InternationalString FinalMoveText = new() { En = "{0}: spread and keep moving", Jp = "{0}: 散開して動く" };
        public bool ShowFinalMoveText = true;
        public MapMarker DpsMarker = MapMarker.B;
        public MapMarker SupportMarker = MapMarker.A;
        public MapMarker AccretionMarker = MapMarker.C;
        public MapMarker FallbackMarker = MapMarker.D;
        public MapMarker LaneDpsMarker = MapMarker.A;
        public MapMarker LaneSupportMarker = MapMarker.D;
        public MapMarker LaneAccretionMarker = MapMarker.B;
        public MapMarker LaneFlexMarker = MapMarker.C;
        public LineBaitDirection DpsLineBaitDirection = LineBaitDirection.Clockwise;
        public LineBaitDirection SupportLineBaitDirection = LineBaitDirection.Counterclockwise;
        public LineBaitDirection AccretionLineBaitDirection = LineBaitDirection.Clockwise;

        public void EnsureDefaults()
        {
            AssignmentMode = NormalizeAssignmentMode(AssignmentMode);
            FirstOrbRole = (FirstOrbRole)Math.Clamp((int)FirstOrbRole, 0, FirstOrbRoleNames.Length - 1);
            LineBaitDirection = (LineBaitDirection)Math.Clamp((int)LineBaitDirection, 0, 1);
            FirstWindowBaitDirection = (FirstWindowBaitDirection)Math.Clamp((int)FirstWindowBaitDirection, 0, FirstWindowBaitDirectionNames.Length - 1);
            FirstPairAssignment = (FirstPairAssignment)Math.Clamp((int)FirstPairAssignment, 0, FirstPairAssignmentNames.Length - 1);
            DpsLineBaitDirection = ClampLineBaitDirection(DpsLineBaitDirection);
            SupportLineBaitDirection = ClampLineBaitDirection(SupportLineBaitDirection);
            AccretionLineBaitDirection = ClampLineBaitDirection(AccretionLineBaitDirection);
            BlackHoleSourceOrder = (BlackHoleSourceOrder)Math.Clamp((int)BlackHoleSourceOrder, 0, 1);
            BlackHoleOrderAnchor = (BlackHoleOrderAnchor)Math.Clamp((int)BlackHoleOrderAnchor, 0, 1);
            FinalInitialBaitMode = (FinalInitialBaitMode)Math.Clamp((int)FinalInitialBaitMode, 0, FinalInitialBaitModeNames.Length - 1);
            FinalInitialNorthRole = (FinalInitialNorthRole)Math.Clamp((int)FinalInitialNorthRole, 0, FinalNorthRoleNames.Length - 1);
            MarkerCommandSource = (MarkerCommandSource)Math.Clamp((int)MarkerCommandSource, 0, MarkerCommandSourceNames.Length - 1);
            // 旧設定からの移行。両方 ON だった人はマスターを優先する (送信量が少ない側)。
            if (MarkerPlacement == MarkerPlacement.Unset)
                MarkerPlacement = IsMaster ? MarkerPlacement.Master
                    : ExecuteMarkerCommand ? MarkerPlacement.EachPlayer
                    : MarkerPlacement.Off;
            MarkerPlacement = (MarkerPlacement)Math.Clamp((int)MarkerPlacement, 0, MarkerPlacementNames.Length - 1);
            DpsMarker = ClampMarker(DpsMarker);
            SupportMarker = ClampMarker(SupportMarker);
            AccretionMarker = ClampMarker(AccretionMarker);
            FallbackMarker = ClampMarker(FallbackMarker);
            LaneDpsMarker = ClampMarker(LaneDpsMarker);
            LaneSupportMarker = ClampMarker(LaneSupportMarker);
            LaneAccretionMarker = ClampMarker(LaneAccretionMarker);
            LaneFlexMarker = ClampMarker(LaneFlexMarker);
            PriorityData ??= CreatePriorityData("P3 Earthquake priority",
                "Used when assignment mode is Priority.", DefaultRolePriority);
            if (MarkerLineOrders == null || MarkerLineOrders.Length != SelectableMarkerIds.Length)
                MarkerLineOrders = [0, 1, 2, 0, 1, 2, 0, 1];
            for (var i = 0; i < MarkerLineOrders.Length; i++)
                MarkerLineOrders[i] = Math.Clamp(MarkerLineOrders[i], 0, BlackHoleOrderNames.Length - 1);
            MarkerDelayMinSeconds = Math.Max(0.0f, MarkerDelayMinSeconds);
            MarkerDelayMaxSeconds = Math.Max(0.0f, MarkerDelayMaxSeconds);
            FirstTargetCommand ??= "/mk attack <me>";
            SecondTargetCommand ??= "/mk bind <me>";
            ThirdTargetCommand ??= "/mk stop <me>";
            AccretionCommand ??= "/mk bind <me>";
            FirstLineWindowText ??= new InternationalString { En = "W{0}: take line at W{1}", Jp = "W{0}: W{1}で線取り" };
            NextLineWindowText ??= new InternationalString { En = "W{0}: take next line", Jp = "W{0}: 次線を取る" };
            TakeLineNowText ??= new InternationalString { En = "W{0}: take line", Jp = "W{0}: 線を取れ" };
            UnknownSlotText ??= new InternationalString { En = "Earthquake slot unknown", Jp = "地震スロット未確定" };
            OverlayText ??= new InternationalString { En = "{0}", Jp = "{0}" };
            FinalCenterText ??= new InternationalString { En = "Center bait", Jp = "中央で誘導" };
            FinalRoleSplitText ??= new InternationalString { En = "{0}: bait", Jp = "{0}: 誘導" };
            FinalSpreadText ??= new InternationalString { En = "{0}: spread", Jp = "{0}: 散開" };
            FinalStackText ??= new InternationalString { En = "Stack center", Jp = "中央で頭割り" };
            FinalTowerText ??= new InternationalString { En = "{0}: tower", Jp = "{0}: 塔" };
            FinalMoveText ??= new InternationalString { En = "{0}: spread and keep moving", Jp = "{0}: 散開して動く" };
        }

        private static MapMarker ClampMarker(MapMarker marker) =>
            (MapMarker)Math.Clamp((int)marker, 0, MapMarkerNames.Length - 1);

        private static LineBaitDirection ClampLineBaitDirection(LineBaitDirection direction) =>
            (LineBaitDirection)Math.Clamp((int)direction, 0, LineBaitDirectionNames.Length - 1);
    }
    #endregion

}
