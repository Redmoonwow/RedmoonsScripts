using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons;
using ECommons.ExcelServices;
using ECommons.GameFunctions;
using ECommons.Hooks.ActionEffectTypes;
using ECommons.ImGuiMethods;
using ECommons.Logging;
using Splatoon;
using Splatoon.SplatoonScripting;
using Splatoon.SplatoonScripting.Priority;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RedmoonsScripts.Duties.Dawntrail.Dancing_Mad;

/// <summary>
/// Dancing Mad (Ultimate) P5 狂気のオーケストラ: 1 回目のホーリーの後、2 回目のホーリーまでの立ち位置を出す。
/// </summary>
/// <remarks>
/// 流れ (クリアログ 3 本と 10/18 の AE ログで確認。時刻は 47952 の詠唱開始から):
///   0.0  狂気のオーケストラ 47952 の詠唱開始 (ケフカ 19511、4.7 秒)
///   5.9  1 回目: フレア 47954 → タンク 2 人、ホーリー 47956 → 非タンク 3 人
///   9.0  2 回目: カオティックフレア 47955 → タンク 2 人、ホーリー 47956 → ケフカに近い 3 人 (仮説)
/// ホーリーは 1 発ずつ別の見えないケフカ (9020) が撃つ。対象を中心にした円 5 (Lumina の EffectRange)。
/// 当たりは中心どうし 5 + 相手の当たり判定 0.5 = 5.5 以内。
/// 2 回目のホーリーは「近い 3 人」に飛ぶので、タンクが対象より近いとタンクに行く (10/18 の全滅)。
///
/// 立ち位置 (時計の 0〜11 時。北が 0 時で 30° 刻み、時計回り):
///   タンクと 2 回目の対象 3 人は、90° おきの十字に並ぶ。タンク → 対象 1 人目 → 2 人目 → 3 人目 の順に時計回り
///   2 回目の対象 (1 回目を受けていない非タンク 3 人) の順番は、優先順 (非タンク 6 人) を上から見て対象だけを拾った順。
///       例: 優先順が 召喚 学者 リーパー … で、召喚が 1 回目を受けたら 学者 → リーパー → …。距離はヒットボックス + HolyOffset
///   タンク 2 人                  十字の頭。ヒットボックス + TankReach (ぎりぎり殴れる位置)
///   1 回目を受けた非タンク 3 人   南の外周 (ケフカから OuterRadius)
/// 十字の向き (Layout):
///   Rotate (既定、タンクも移動させて移動距離を均等化する)  十字を 30° 刻みで回し、仮定の出発点から動く量が少ない向き。長く動くのは M2 / M1 の枠に回す。
///       出発点 (仮定): タンク 北、優先順の 1〜6 番目 = 北東 / 東 / 南東 / 南 / 南西 / 西 (8 方向散開)、半径 SpreadRadius。
///       実際の位置を使わないのは、各自の画面で別々に計算しても同じ答えになるようにするため。
///       決め方: 遠隔・ヒーラーの枠 (優先順の 3・4 番目以外) の一番大きい移動が短い → 移動の合計 (タンク + 3 人) が短い → 0 時に近い
///       例: 東 (R2 H2 M2) → タンク 10 時 / R2 1 時 / H2 4 時 / M2 7 時、西 (M1 H1 R1) → タンク 1 時 / M1 4 時 / H1 7 時 / R1 10 時
///   TankFixed (タンク A 固定)          タンク 0 時 / 対象 3 時 / 6 時 / 9 時
/// ケフカの位置とヒットボックスは毎フレーム実物から取る (実測 8.01。ModelSkeleton × Scale の計算式の 4.5 は外れ)。
/// ケフカの円は大きく、クリアログの 2 回目の対象は円の内側 (4.5〜6.8) に立っている。タンクは近接の届く 8.01 + 3 まで下がれる。
/// 8 人全員の担当を解いてから、自分の点だけを描く。
///
/// 優先順が使えない / 2 回目の対象が 3 人にならないときは、2 回目の対象の点を出さない (履歴に理由を残す)。
///
/// 2 回目のホーリーが来たら、実際に当たった人・その瞬間の距離・近い 3 人を記録して「判定」に残す。
/// 仮説 (近い 3 人に飛ぶ) と予定 (割り振り) の両方と突き合わせる。
///
/// 進行はゲームの出来事だけで決める (時間で決めない。リプレイの一時停止・スローでもずれない):
///   開始       47952 の詠唱
///   割り振り   1 回目のホーリーで非タンク 3 人が中心になった瞬間。そろわなければ ついでにフレア/ホーリー (5350/5351) が付いた時
///   2 回目     47953 (オーケストラの 2 発目) の後のホーリー。47953 を取り損ねても、1 回目の中心でない人が中心なら 2 回目
///   点を消す   2 回目のホーリーの 1 発目の ActionEffect
///   判定・終了 2 回目の中心が 3 人そろった時。そろわなければ ついでに… が切れた時 / フレアディフュージョン 47957
/// 時間を使うのは、点の文字の残り秒と、実機の保険 (SafetySeconds) だけ。
///
/// 設定画面: ユーザー向けは配置 (Layout) と優先順だけ。点の見た目は Splatoon の Registered elements の Edit で変える
/// (スクリプトは位置・表示・文字と、グラデーションが ON のときの色しか書かない)。
/// Debug 欄は 5 タブ:
///   設定   点の距離 (ホーリー・タンク・外周)、仮定の出発点の半径、時間切れ、グラデーション、文字、Verbose log、Debug 表示、今の余地
///   現在   状態、ケフカ、十字の向き、5 つの点の座標、8 人の表 (役・担当・1 回目の角度と距離・今の距離と近い順・2 回目・引かれる場所・判定結果)、
///          このトライの判定 (1 回目・2 回目のオーケストラ)。判定は毎回 dalamud.log にも出す
///   テスト 1 回目の対象を手で選んで、本物と同じ道で点を出す (今のターゲットをケフカの代わりにする)
///   組み合わせ 2 回目の対象 3 人の 20 通りごとの割り振り (本番と同じ計算)。「流す」でその組み合わせの点を出す
/// テスト中と Debug 表示 ON のときは、十字の 5 人 (タンク 2 人 + 2 回目の対象 3 人) それぞれから、その人の点へ線 (TestTether0〜4) も引く
///   履歴   判断ログ。VerboseLog が切れていても残す
/// </remarks>
internal unsafe class P5_Orchestra_Holy : SplatoonScript<P5_Orchestra_Holy.Config>
{
    #region types
    /********************************************************************/
    /* types                                                            */
    /********************************************************************/
    private enum State
    {
        None = 0,
        WaitHoly1,     // 47952 の詠唱を見た。1 回目のホーリー待ち
        CollectHoly1,  // 1 回目の中心が 3 人そろうか、ついでに… が付くのを待っている
        Guide,         // 点を出している。2 回目のホーリー待ち
        CollectHoly2,  // 2 回目の中心が 3 人そろうか、ついでに… が切れる / ディフュージョンを待っている (判定用)
    }

    private enum Role { None = 0, Holy2, Holy1, Tank }

    /// <summary>点の種類。位置は Clock (0〜11 時) と組で決まる。</summary>
    private enum Spot { None = 0, Tank, Holy, Outer }

    private sealed class PartyData
    {
        public uint EntityId;
        public string Name = "";
        public Job Job;
        public bool IsTank;
        public bool Mine;             // BasePlayer (Base Player Override を含む) かどうか。RefreshMine で毎フレーム決め直す

        public bool Holy1Target;      // 1 回目のホーリーの中心にされた
        public int Holy1Hits;         // 1 回目のホーリーに当たった数 (巻き込み含む)
        public float Holy1Angle = float.NaN;    // 1 回目の時点の角度 (北 0°、時計回り)
        public float Holy1Radius = float.NaN;   // 1 回目の時点のケフカからの距離

        public Role Role;
        public Spot Spot;
        public int Clock = -1;        // 点の時計の位置 (0〜11 時)。Spot と組
        public int Order = -1;        // 2 回目の対象の中の順番 (0 = 十字でタンクの次)

        public bool Holy2Target;
        public int Holy2Hits;
        public float Holy2Radius = float.NaN;   // 2 回目の着弾の瞬間のケフカからの距離

        public float LiveRadius = float.NaN;    // 今のケフカからの距離
        public int LiveRank = -1;               // 今の近い順 (0 始まり)

        public IPlayerCharacter? Object => EntityId.GetObject() as IPlayerCharacter;
    }

    private sealed class Judge
    {
        public string Label = "";         // 「プル 23:46 / 2 回目のオーケストラ」
        public string Time = "";
        public string Planned = "";       // 割り振りでの 2 回目の対象
        public string Targets = "";       // 実際にホーリーの中心にされた人
        public string Hits = "";          // 当たった人 (巻き込み含む)
        public string Nearest = "";       // 着弾の瞬間の近い 3 人
        public bool PlannedMatch;
        public bool NearestMatch;
        public string Mine = "";
    }
    #endregion

    #region class
    /********************************************************************/
    /* class                                                            */
    /********************************************************************/
    /// <summary>十字 (タンク + 2 回目の対象 3 人) の向き。</summary>
    public enum LayoutMode
    {
        TankFixed = 0,   // タンク A 固定: タンク 0 時 / 3 時 / 6 時 / 9 時 (値は保存済みの設定と合わせるため変えない)
        Rotate = 1,      // 既定。タンクも移動させて移動距離を均等化する (BestRotation)
    }

    public class Config
    {
        public LayoutMode Layout = LayoutMode.Rotate;   // 既定は Rotate (固定 PT の決め方)。保存済みの設定はその値のまま
        public float SpreadRadius = 10.5f;    // Rotate の仮定の出発点 (8 方向散開) の半径。リプレイの実測は 10〜11.6
        // 既定は R2 → H2 → M2 → M1 → H1 → R1 (ロール指定。誰がどのロールかは Splatoon の Edit Roles で割り当てる)
        public PriorityData6 Priority = new()
        {
            Name = "P5 Orchestra Holy",
            Description = "非タンク 6 人。上から見て 2 回目のホーリーの対象だけを拾った順に 3 時 → 6 時 → 9 時",
            PriorityLists =
            [
                new PriorityList
                {
                    IsRole = true,
                    List =
                    [
                        new JobbedPlayer { Role = RolePosition.R2 },
                        new JobbedPlayer { Role = RolePosition.H2 },
                        new JobbedPlayer { Role = RolePosition.M2 },
                        new JobbedPlayer { Role = RolePosition.M1 },
                        new JobbedPlayer { Role = RolePosition.H1 },
                        new JobbedPlayer { Role = RolePosition.R1 },
                    ],
                },
            ],
        };
        public float HolyOffset = -0.5f;      // 2 回目の対象の点: ヒットボックスの縁から外へ (負なら内側)
        public float TankReach = 3.0f;        // タンクの点: ヒットボックスの縁から外へ (近接は縁から 3)
        public float OuterRadius = 19f;       // 1 回目を受けた人の点: ケフカから (フィールドは半径 20 と推定)
        public float FallbackHitbox = 8.01f;  // ケフカが見つからないときのヒットボックス (P5 ケフカ 19511 の実測)
        // 点の見た目 (半径・太さ・色など) は Splatoon の Registered elements の Edit で変える。スクリプトは書かない。
        // 例外はグラデーション (毎フレーム色を書く) と文字 (担当と秒を書く)。どちらも切れる
        public bool UseGradient = true;
        public uint Color1 = 0xC8FFFF00;      // グラデーション (ABGR)。2 色を同じにすると単色
        public uint Color2 = 0xC8FF00FF;
        public bool ShowText = true;
        public float SafetySeconds = 15f;     // 保険。ふつうはイベント (2 回目のホーリー / ついでに… が切れる / ディフュージョン) で終わる。実機だけ
        public bool VerboseLog;               // 判断を dalamud.log にも出す
        public bool DebugDraw;                // 全員の点・ホーリーの範囲・ヒットボックスを細く出す
    }

    public sealed class PriorityData6 : PriorityData
    {
        public override int GetNumPlayers() => 6;
    }
    #endregion

    #region const
    /********************************************************************/
    /* const                                                            */
    /********************************************************************/
    // 進行はすべてゲームの出来事で決める (時間で決めるのは、点の文字の残り秒と実機の保険だけ)
    private const uint OrchestraCast = 47952;   // 狂気のオーケストラ (詠唱 4.7 秒) → 開始
    private const uint OrchestraSecond = 47953; // 狂気のオーケストラの 2 発目 (2 回目のホーリーの約 0.9 秒前) → 次のホーリーは 2 回目
    private const uint HolyId = 47956;          // ホーリー
    private const uint FlareDiffusion = 47957;  // フレアディフュージョン (2 回目の約 3.5 秒後) → 必ず畳む
    private const ushort SurpriseFlare = 5350;  // ついでにフレア (1 回目の約 0.6 秒後に付き、6 秒で切れる)
    private const ushort SurpriseHoly = 5351;   // ついでにホーリー (同上)
    private const float HolyRange = 5f;         // ホーリーの円 (Lumina の EffectRange)
    private const float PlayerHitbox = 0.5f;
    private const long ExpectedHoly2Ms = 3170;   // 1 回目 → 2 回目 (クリアログ 3.16〜3.17 秒、10/18 は 3.18 秒)。点の文字の残り秒だけに使う
    private const int HistoryMax = 300;
    private static readonly Vector3 ArenaCenter = new(100f, 0f, 100f);

    private const int OuterClock = 6;            // 1 回目を受けた人の外周の点は南 (6 時) 固定
    private const string PointElement = "Point";
    private const int TestTetherCount = 5;       // テスト中の線: タンク 2 人 + 2 回目の対象 3 人
    #endregion

    #region public properties
    /********************************************************************/
    /* public properties                                                */
    /********************************************************************/
    public override HashSet<uint>? ValidTerritories { get; } = [1363];   // Dancing Mad (Ultimate)
    public override Metadata Metadata => new(17, "Redmoon");
    #endregion

    #region private properties
    /********************************************************************/
    /* private properties                                               */
    /********************************************************************/
    private State _state = State.None;
    private readonly List<PartyData> _partyDataList = [];
    private uint _kefkaId;
    private long _startMs;
    private long _holy1Ms;                      // 1 回目のホーリーの時刻。点の文字の残り秒だけに使う
    private bool _orchestra2Seen;               // 47953 を見た。次のホーリーは 2 回目
    private bool _test;
    private string _assignNote = "";            // 割り振りの根拠 / 失敗理由
    private int _rotation;                      // 十字の向き = タンクの時計の位置 (0〜11)。TankFixed は 0
    private string _rotationNote = "";          // Rotate の各向きの移動の合計
    private int _orchestraNo;                   // このプルで何回目のオーケストラか (1 プルで 2 回。テストは数えない)
    private string _pullTime = "-";             // このプルの戦闘開始の時刻
    private readonly List<Judge> _judges = [];  // このトライの分だけ (古い順、ふつう 2 件)。戦闘開始で捨てる
    private readonly List<string> _history = [];
    private readonly HashSet<uint> _testHoly1 = [];
    #endregion

    #region public methods
    /********************************************************************/
    /* public methods                                                   */
    /********************************************************************/
    public override void OnSetup()
    {
        Controller.RegisterElement(PointElement, new Element(0)
        {
            Enabled = false,
            radius = 0.6f,
            thicc = 15f,
            fillIntensity = 0.25f,
            tether = true,
            overlayBGColor = 0xC8000000,
            overlayTextColor = 0xFFFFFFFF,
            overlayVOffset = 2.4f,
            overlayFScale = 1.5f,
        });
        // Debug 用。5 つの点 (タンク / ホーリー 3 つ / 外周) / ホーリーの点ごとの範囲 / ケフカのヒットボックス
        for (var i = 0; i < 5; i++)
            Controller.RegisterElement($"Dbg{i}", new Element(0)
            {
                Enabled = false,
                radius = 0.4f,
                thicc = 2f,
                color = 0xC8FFFFFF,
                overlayTextColor = 0xFFFFFFFF,
                overlayVOffset = 1.5f,
            });
        for (var i = 0; i < 3; i++)
            Controller.RegisterElement($"DbgReach{i}", new Element(0)
            {
                Enabled = false,
                radius = HolyRange + PlayerHitbox,
                thicc = 2f,
                Filled = false,
                color = 0x9600FFFF,
            });
        Controller.RegisterElement("DbgHitbox", new Element(0)
        {
            Enabled = false,
            thicc = 2f,
            Filled = false,
            color = 0x96FFFFFF,
        });
        // テスト中 / Debug 表示中: 十字の 5 人 (タンク 2 人 + 2 回目の対象 3 人) それぞれから、その人の点への線
        for (var i = 0; i < TestTetherCount; i++)
            Controller.RegisterElement($"TestTether{i}", new Element(2)
            {
                Enabled = false,
                radius = 0f,
                thicc = 6f,
                color = 0xC8FFFF00,
                LineEndB = Splatoon.Serializables.LineEnd.Arrow,
                overlayBGColor = 0xC8000000,
                overlayTextColor = 0xFFFFFFFF,
                overlayVOffset = 1.5f,
            });
    }

    public override void OnStartingCast(uint source, uint castId)
    {
        if (castId == OrchestraCast) Begin(source, false);
    }

    public override void OnActionEffectEvent(ActionEffectSet set)
    {
        if (_state == State.None || _test || set.Action is not { } action) return;
        switch (action.RowId)
        {
            case HolyId:
                OnHoly(set);
                break;
            case OrchestraSecond:
                _orchestra2Seen = true;
                Log("47953 (オーケストラの 2 発目): 次のホーリーを 2 回目とみなす");
                break;
            case FlareDiffusion:
                // 2 回目の約 3.5 秒後。ここまで来たら必ず畳む
                if (_state == State.CollectHoly2) JudgeHoly2();
                Finish(_state == State.CollectHoly2 ? "フレアディフュージョン" : "フレアディフュージョン (2 回目のホーリーを取り損ねた)");
                break;
        }
    }

    /// <summary>ついでにフレア / ホーリーが付いた = 1 回目の着弾は終わっている。3 発そろわなくても (誰かが死んでいる) 割り振る。</summary>
    public override void OnGainBuffEffect(uint sourceId, FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        if (_test || _state != State.CollectHoly1 || status.StatusId is not (SurpriseFlare or SurpriseHoly)) return;
        Log($"ついでに… が付いた時点で 1 回目の中心が {_partyDataList.Count(x => x.Holy1Target && !x.IsTank)} 人。そのまま割り振る");
        Resolve();
        _state = State.Guide;
    }

    /// <summary>ついでにフレア / ホーリーが切れた = 2 回目の着弾は終わっている (約 2.8 秒後)。</summary>
    public override void OnRemoveBuffEffect(uint sourceId, FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        if (_test || status.StatusId is not (SurpriseFlare or SurpriseHoly)) return;
        if (_state == State.CollectHoly2)
        {
            JudgeHoly2();
            Finish("ついでに… が切れた");
        }
        else if (_state is State.Guide or State.CollectHoly1)
            Finish("ついでに… が切れた (2 回目のホーリーを取り損ねた)");
    }

    public override void OnUpdate()
    {
        if (_state == State.None) return;
        var now = Environment.TickCount64;
        // 保険。進行はイベントで決めるので、ふつうはここに来ない。実時間なのでリプレイでは使わない
        // (一時停止・スロー・シークで実時間と合わない。シークでは Splatoon が OnReset を呼ぶので、そこで畳まれる)
        if (!Replay && now - _startMs > C.SafetySeconds * 1000f)
        {
            Finish("保険の時間切れ (イベントを取り損ねた)");
            return;
        }
        UpdateLive();
        if (_state == State.Guide) ShowPoints(now);
    }

    public override void OnReset()
    {
        if (_state != State.None) Finish("reset");
    }

    /// <summary>プルの頭で前のプルの表を捨てる。判定と履歴は前のプルの確認用に残す。</summary>
    public override void OnCombatStart()
    {
        if (_state != State.None) Finish("戦闘開始");
        HidePoints();
        _partyDataList.Clear();
        _kefkaId = 0;
        _startMs = 0;
        _holy1Ms = 0;
        _orchestra2Seen = false;
        _assignNote = "";
        _orchestraNo = 0;
        _pullTime = DateTime.Now.ToString("HH:mm:ss");
        _judges.Clear();   // 判定はこのトライの分だけ。前のトライの分は dalamud.log にある
        Log("戦闘開始: 初期化");
    }

    /// <summary>Registered elements の上書きが Enabled=true で保存されていても、読み込み直後に出ないように。</summary>
    public override void OnEnable() => HidePoints();

    public override void OnDisable()
    {
        if (_state != State.None) Finish("disable");
        HidePoints();
    }

    public override void OnSettingsDraw()
    {
        DrawSettings();
        if (!ImGuiEx.CollapsingHeader("Debug")) return;
        ImGuiEx.EzTabBar("##P5OHDebug",
            ("設定", DrawConfigTab, null, false),
            ("現在", DrawNowTab, null, false),
            ("テスト", DrawTestTab, null, false),
            ("組み合わせ", DrawComboTab, null, false),
            ("履歴", DrawHistoryTab, null, false));
    }
    #endregion

    #region private methods
    /********************************************************************/
    /* private methods                                                  */
    /********************************************************************/

    // ---- 進行 -----------------------------------------------------------------

    private void Begin(uint kefkaId, bool test)
    {
        if (_state != State.None) Finish("次の詠唱");
        _kefkaId = kefkaId;
        _startMs = Environment.TickCount64;
        _holy1Ms = 0;
        _orchestra2Seen = false;
        _test = test;
        _assignNote = "";
        _rotation = 0;
        _rotationNote = "";
        if (!test) _orchestraNo++;
        BuildParty();
        _state = State.WaitHoly1;
        Log($"開始{(test ? " (テスト)" : $" ({_orchestraNo} 回目のオーケストラ)")}: ケフカ {NameOf(kefkaId)} 0x{kefkaId:X8} / パーティ {_partyDataList.Count} 人 / 自分 {_partyDataList.Find(x => x.Mine)?.Name ?? "居ない"}");
    }

    private void Finish(string reason)
    {
        HidePoints();
        Log($"終了: {reason}");
        _state = State.None;
        _test = false;
    }

    private void BuildParty()
    {
        _partyDataList.Clear();
        var me = BasePlayer?.EntityId ?? 0;
        foreach (var pc in FakeParty.Get())
        {
            var job = (Job)pc.ClassJob.RowId;
            _partyDataList.Add(new PartyData
            {
                EntityId = pc.EntityId,
                Name = pc.Name.ToString(),
                Job = job,
                IsTank = job.IsTank(),
                Mine = pc.EntityId == me,
            });
        }
    }

    private void RecordHoly1(ActionEffectSet set)
    {
        var (target, how) = CenterOf(set);
        if (target != null) target.Holy1Target = true;
        foreach (var e in set.TargetEffects)
            if (_partyDataList.Find(x => x.EntityId == (uint)e.TargetID) is { } hit) hit.Holy1Hits++;
        Log($"1 回目のホーリー: 中心 {target?.Name ?? "不明"} ({how}) / 当たり {string.Join(", ", set.TargetEffects.Select(e => NameOf((uint)e.TargetID)))}");
    }

    private void RecordHoly2(ActionEffectSet set)
    {
        var (target, how) = CenterOf(set);
        if (target != null) target.Holy2Target = true;
        foreach (var e in set.TargetEffects)
            if (_partyDataList.Find(x => x.EntityId == (uint)e.TargetID) is { } hit) hit.Holy2Hits++;
        Log($"2 回目のホーリー: 中心 {target?.Name ?? "不明"} ({how}) / 当たり {string.Join(", ", set.TargetEffects.Select(e => NameOf((uint)e.TargetID)))}");
    }

    /// <summary>ホーリーの中心にされた人。ふつうは AnimationTarget。巻き込まれた人は TargetEffects にだけ居る。</summary>
    /// <remarks>AnimationTarget がパーティに居ないとき (撃った側自身など) は、当たった人のうち着弾位置に一番近い人。</remarks>
    private (PartyData? Target, string How) CenterOf(ActionEffectSet set)
    {
        if (set.Target is { } t && _partyDataList.Find(x => x.EntityId == t.EntityId) is { } byTarget)
            return (byTarget, "AnimationTarget");
        var hits = set.TargetEffects
            .Select(e => _partyDataList.Find(x => x.EntityId == (uint)e.TargetID))
            .OfType<PartyData>()
            .Where(x => x.Object != null)
            .OrderBy(x => Distance2D(x.Object!.Position, set.Position))
            .ToList();
        return hits.Count > 0 ? (hits[0], $"着弾位置に一番近い当たり / AnimationTarget={set.Target?.Name}") : (null, $"AnimationTarget={set.Target?.Name}");
    }

    /// <summary>ホーリー 1 発 (1 パケット = 1 発)。1 回目か 2 回目かは状態と IsHoly2 で決める。</summary>
    private void OnHoly(ActionEffectSet set)
    {
        switch (_state)
        {
            case State.WaitHoly1:
                _holy1Ms = Environment.TickCount64;   // 点の文字の残り秒だけに使う
                _state = State.CollectHoly1;
                RecordHoly1(set);
                ResolveIfHoly1Complete();
                break;
            case State.CollectHoly1:
                RecordHoly1(set);
                ResolveIfHoly1Complete();
                break;
            case State.Guide when !IsHoly2(set):
                // 割り振った後に来た 1 回目の 1 発 (ついでに… で見切って割り振った後など)。割り振りはそのまま
                RecordHoly1(set);
                Log("1 回目のホーリーが割り振りの後に来た (割り振りはそのまま)");
                break;
            case State.Guide:
                SnapshotHoly2Radius();
                _state = State.CollectHoly2;
                HidePoints();   // この瞬間に当たりが決まっている。点はここで消す
                RecordHoly2(set);
                JudgeIfHoly2Complete();
                break;
            case State.CollectHoly2:
                RecordHoly2(set);
                JudgeIfHoly2Complete();
                break;
        }
    }

    /// <summary>非タンク 3 人が 1 回目の中心になった時点で割り振る (待たない。動ける時間を削らない)。</summary>
    /// <remarks>3 人にならない (誰かが死んでいる) ときは、ついでに… が付いた時 (OnGainBuffEffect) に割り振る。</remarks>
    private void ResolveIfHoly1Complete()
    {
        if (_partyDataList.Count(x => x.Holy1Target && !x.IsTank) < 3) return;
        Resolve();
        _state = State.Guide;
    }

    /// <summary>割り振りの後のホーリーが 2 回目か。47953 を見ていれば 2 回目。見ていなくても、中心が 1 回目の中心でなければ 2 回目。</summary>
    private bool IsHoly2(ActionEffectSet set)
    {
        if (_orchestra2Seen) return true;
        var (center, _) = CenterOf(set);
        return center != null && !center.Holy1Target;
    }

    /// <summary>2 回目の中心が 3 人そろったら判定して畳む。そろわなければ ついでに… が切れた時 / ディフュージョンで判定する。</summary>
    private void JudgeIfHoly2Complete()
    {
        if (_partyDataList.Count(x => x.Holy2Target) < 3) return;
        JudgeHoly2();
        Finish("2 回目のホーリー");
    }

    private void SnapshotHoly2Radius()
    {
        var (center, _) = Kefka();
        foreach (var pc in _partyDataList)
            if (pc.Object is { } o) pc.Holy2Radius = Distance2D(o.Position, center);
    }

    // ---- 割り振り (8 人全員) -------------------------------------------------

    /// <summary>1 回目の着弾がそろった時点で、8 人全員の役と担当を決める。</summary>
    private void Resolve()
    {
        var (center, hitbox) = Kefka();
        foreach (var pc in _partyDataList)
        {
            if (pc.Object is { } o)
            {
                pc.Holy1Angle = ClockAngle(o.Position, center);
                pc.Holy1Radius = Distance2D(o.Position, center);
            }
            pc.Role = pc.IsTank ? Role.Tank : pc.Holy1Target ? Role.Holy1 : Role.Holy2;
            pc.Spot = pc.Role switch { Role.Tank => Spot.Tank, Role.Holy1 => Spot.Outer, _ => Spot.None };
            pc.Clock = pc.Role == Role.Holy1 ? OuterClock : -1;
            pc.Order = -1;
        }

        var tanks = _partyDataList.Count(x => x.Role == Role.Tank);
        var holy1 = _partyDataList.Count(x => x.Role == Role.Holy1);
        if (tanks != 2) Log($"警告: タンクが {tanks} 人");
        if (holy1 != 3) Log($"警告: 1 回目を受けた非タンクが {holy1} 人 (中心: {string.Join(", ", _partyDataList.Where(x => x.Holy1Target).Select(x => x.Name))})");

        // 十字の向きは 2 回目の対象と一緒に決まる。決まった向きの頭にタンクを置く
        _assignNote = AssignHoly2(hitbox);
        foreach (var pc in _partyDataList.Where(x => x.Role == Role.Tank)) pc.Clock = _rotation;
        Log($"割り振り ({C.Layout}): {_assignNote} / タンク {_rotation} 時 / ケフカ ({center.X:F2}, {center.Z:F2}) ヒットボックス {hitbox:F2}");
        if (_rotationNote != "") Log($"  向きの候補: {_rotationNote}");
        foreach (var pc in _partyDataList)
            Log($"  {pc.Name} {pc.Job} {pc.Role} {SpotText(pc.Spot, pc.Clock)} 角度 {pc.Holy1Angle:F0}° 距離 {pc.Holy1Radius:F1}{(pc.Mine ? " (自分)" : "")}");
    }

    /// <summary>
    /// 2 回目の対象 3 人を、優先順の上から拾った順に、十字のタンクの次から時計回りに置く。十字の向き (_rotation) もここで決める。
    /// 優先順が使えなければ理由を返して点は出さない (タンクは 0 時のまま)。
    /// </summary>
    private string AssignHoly2(float hitbox)
    {
        _rotation = 0;
        _rotationNote = "";
        var candidates = _partyDataList.Where(x => x.Role == Role.Holy2).ToList();
        if (candidates.Count != 3) return $"失敗: 2 回目の対象が {candidates.Count} 人";

        // 優先順の 6 人の並び (Rotate の仮定の出発点にも使う)。2 回目の対象だけを上から拾う
        var all = C.Priority.GetPlayers(_ => true);
        if (all == null) return "失敗: 優先順が使えない (6 枠すべてが埋まっていて、6 人全員がパーティに居ること)";
        var line = all.Select(x => _partyDataList.Find(p => p.EntityId == x.IGameObject?.EntityId)).ToList();
        var list = line.OfType<PartyData>().Where(x => x.Role == Role.Holy2).Distinct().ToList();
        if (list.Count != 3)
            return $"失敗: 優先順で 3 人が並ばない ({list.Count} 人: {string.Join(", ", list.Select(x => x.Name))} / 対象: {string.Join(", ", candidates.Select(x => x.Name))})";

        if (C.Layout == LayoutMode.Rotate)
            _rotation = BestRotation(list.Select(x => line.IndexOf(x)).ToArray(), hitbox);

        for (var i = 0; i < 3; i++)
        {
            list[i].Order = i;
            list[i].Spot = Spot.Holy;
            list[i].Clock = (_rotation + 3 * (i + 1)) % 12;
        }
        return string.Join(" / ", list.Select(x => $"{SpotText(x.Spot, x.Clock)} ← {x.Name}"));
    }

    /// <summary>
    /// Rotate: 十字を 30° 刻みで回したときの、タンクの時計の位置。決め方 (各自の画面で同じ答えになるよう、決まった順で比べる):
    ///   1. 遠隔・ヒーラーの枠 (優先順の 3・4 番目 = 南東・南の M2 / M1 以外) の対象のうち、一番大きい移動が短い
    ///   2. 移動の合計 (タンク + 対象 3 人) が短い
    ///   3. 0 時に近い
    /// 長く動く役 (十字と 8 方向散開がずれる分) は M1 / M2 の枠に回る。
    /// 1 回目は東西に分かれる (2 回目の対象は 東の R2 H2 M2 か 西の M1 H1 R1) 想定で、
    ///   東 → タンク 10 時 / R2 1 時 / H2 4 時 / M2 7 時 (M2 が 2.5 時間分)、西 → タンク 1 時 / M1 4 時 / H1 7 時 / R1 10 時 (M1 が 2 時間分)。
    /// 出発点はタンク 北、優先順の i 番目 (0 始まり) = 北から 45° × (i + 1)、半径 SpreadRadius。点の半径は本番と同じ。
    /// </summary>
    /// <param name="lineIndex">2 回目の対象 3 人の、優先順の中の位置 (十字に置く順)。</param>
    private int BestRotation(int[] lineIndex, float hitbox)
    {
        var best = ComputeRotation(lineIndex, hitbox, out _rotationNote);
        return best;
    }

    /// <summary>BestRotation の計算そのもの (状態を変えない)。組み合わせタブの表からも呼ぶ。</summary>
    private int ComputeRotation(int[] lineIndex, float hitbox, out string note)
    {
        var rh = hitbox + C.HolyOffset;
        var rt = hitbox + C.TankReach;
        var rs = C.SpreadRadius;
        var best = 0;
        var bestKey = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var notes = new List<string>();
        for (var c = 0; c < 12; c++)
        {
            var sum = Vector3.Distance(DirOf(0f) * rs, DirOf(c * 30f) * rt);
            var maxRh = 0f;
            for (var i = 0; i < 3; i++)
            {
                var d = Vector3.Distance(DirOf(StartAngle(lineIndex[i])) * rs, DirOf((c + 3 * (i + 1)) % 12 * 30f) * rh);
                sum += d;
                if (lineIndex[i] is not (2 or 3)) maxRh = MathF.Max(maxRh, d);   // M2 / M1 の枠は長く動いてよい
            }
            var key = new[] { maxRh, sum, Math.Min(c, 12 - c) };
            notes.Add($"{c}時 {maxRh:F1}/{sum:F1}");
            if (!IsBetter(key, bestKey)) continue;
            best = c;
            bestKey = key;
        }
        note = $"タンク {best} 時 (遠隔・ヒーラーの最大 {bestKey[0]:F1} / 合計 {bestKey[1]:F1}) ← 候補 (最大/合計): {string.Join(", ", notes)}";
        return best;
    }

    /// <summary>仮定の出発点 (8 方向散開) の角度。優先順の i 番目 (0 始まり) = 北から 45° × (i + 1)。タンクは 0°。</summary>
    private static float StartAngle(int lineIndex) => 45f * (lineIndex + 1);

    /// <summary>2 つの角度 (度) の差を時計の時間 (30° = 1 時間) で。</summary>
    private static float ClockHours(float fromDeg, float toDeg)
    {
        var d = MathF.Abs(fromDeg - toDeg) % 360f;
        return (d > 180f ? 360f - d : d) / 30f;
    }

    /// <summary>key を前から順に比べて小さい方。差が 0.01 以下は同じとみなす。</summary>
    private static bool IsBetter(float[] key, float[] best)
    {
        for (var j = 0; j < key.Length; j++)
        {
            if (key[j] < best[j] - 0.01f) return true;
            if (key[j] > best[j] + 0.01f) return false;
        }
        return false;
    }

    private void JudgeHoly2()
    {
        var planned = _partyDataList.Where(x => x.Spot == Spot.Holy).ToList();
        var targets = _partyDataList.Where(x => x.Holy2Target).ToList();
        var nearest = _partyDataList.Where(x => !float.IsNaN(x.Holy2Radius)).OrderBy(x => x.Holy2Radius).Take(3).ToList();
        var mine = _partyDataList.Find(x => x.Mine);
        var (_, hitbox) = Kefka();
        var judge = new Judge
        {
            Label = $"プル {_pullTime} / {_orchestraNo} 回目のオーケストラ",
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Planned = string.Join(", ", planned.Select(x => $"{x.Name} ({SpotText(x.Spot, x.Clock)})")),
            Targets = string.Join(", ", targets.Select(x => x.Name)),
            Hits = string.Join(", ", _partyDataList.Where(x => x.Holy2Hits > 0).Select(x => $"{x.Name}×{x.Holy2Hits}")),
            Nearest = string.Join(", ", nearest.Select(x => $"{x.Name} {x.Holy2Radius:F2}")),
            PlannedMatch = planned.Count == 3 && planned.All(x => x.Holy2Target) && targets.Count == 3,
            NearestMatch = nearest.Count == 3 && nearest.All(x => x.Holy2Target),
            Mine = mine == null ? "自分が居ない"
                : $"{mine.Role} {SpotText(mine.Spot, mine.Clock)} / 着弾時の距離 {mine.Holy2Radius:F2} (点 {PointRadiusOf(mine.Spot, hitbox):F2}) / 当たり {mine.Holy2Hits}",
        };
        _judges.Add(judge);
        var line = $"判定 ({judge.Label}): 予定どおり={judge.PlannedMatch} 近い 3 人={judge.NearestMatch} / 中心 {judge.Targets} / 近い順 {judge.Nearest} / 自分 {judge.Mine}";
        Log(line);
        // 前のトライの判定はここにしか残らないので、VerboseLog に関係なく dalamud.log へ出す (1 回 1 行)
        if (!C.VerboseLog) PluginLog.Information($"[P5OH] {line}");
    }

    // ---- 表示 -----------------------------------------------------------------

    private void ShowPoints(long now)
    {
        var (center, hitbox) = Kefka();
        var left = (_holy1Ms + ExpectedHoly2Ms - now) / 1000f;

        var mine = _partyDataList.Find(x => x.Mine);
        // 位置・表示・文字・(グラデーションなら) 色だけ書く。半径や太さは Registered elements の Edit に任せる
        if (Controller.TryGetElementByName(PointElement, out var e))
        {
            // Base Player Override で担当の無い人に切り替えたら、前の人の点を残さない
            e.Enabled = mine is { Spot: not Spot.None };
            if (mine is { Spot: not Spot.None })
            {
                e.SetRefPosition(PointOf(mine.Spot, mine.Clock, center, hitbox));
                if (C.UseGradient) e.color = GradientColor.Get(C.Color1.ToVector4(), C.Color2.ToVector4()).ToUint();
                // 秒は実時間なので、リプレイでは出さない
                var text = SpotText(mine.Spot, mine.Clock);
                e.overlayText = !C.ShowText ? "" : Replay ? text : $"{text} {Math.Max(left, 0f):F1}";
            }
        }

        ShowTestTethers(center, hitbox);

        if (!C.DebugDraw)
        {
            // 途中で切られたら、出していた Debug 用の円を消す
            foreach (var x in Controller.GetRegisteredElements())
                if (x.Key.StartsWith("Dbg")) x.Value.Enabled = false;
            return;
        }
        var points = LayoutPoints().ToList();
        for (var i = 0; i < points.Count; i++)
            if (Controller.TryGetElementByName($"Dbg{i}", out var d))
            {
                var (spot, clock) = points[i];
                d.Enabled = true;
                d.SetRefPosition(PointOf(spot, clock, center, hitbox));
                var who = _partyDataList.Where(x => x.Spot == spot && x.Clock == clock).Select(x => x.Name.Split(' ')[0]);
                d.overlayText = $"{SpotText(spot, clock)}: {string.Join(",", who)}";
            }
        var holy = points.Where(x => x.Spot == Spot.Holy).ToList();
        for (var i = 0; i < holy.Count; i++)
            if (Controller.TryGetElementByName($"DbgReach{i}", out var r))
            {
                r.Enabled = true;
                r.SetRefPosition(PointOf(Spot.Holy, holy[i].Clock, center, hitbox));
            }
        if (Controller.TryGetElementByName("DbgHitbox", out var h))
        {
            h.Enabled = true;
            h.radius = hitbox;   // ヒットボックスそのものを見せる円なので、半径はスクリプトが書く
            h.SetRefPosition(center);
        }
    }

    /// <summary>
    /// テスト中 (テスト / 組み合わせタブの「流す」) か Debug 表示 ON のとき、十字の 5 人 (タンク 2 人 + 2 回目の対象 3 人) それぞれのキャラから、
    /// その人の点へ線を引く。自分以外がどこへ行くかを一度に見るため。それ以外のときは消す。
    /// </summary>
    private void ShowTestTethers(Vector3 center, float hitbox)
    {
        var show = _test || C.DebugDraw;
        var cross = _partyDataList.Where(x => x.Spot == Spot.Tank)
            .Concat(_partyDataList.Where(x => x.Spot == Spot.Holy).OrderBy(x => x.Order))
            .ToList();
        for (var i = 0; i < TestTetherCount; i++)
        {
            if (!Controller.TryGetElementByName($"TestTether{i}", out var t)) continue;
            var pc = show && i < cross.Count ? cross[i] : null;
            t.Enabled = pc?.Object != null;
            if (pc?.Object is not { } o) continue;
            t.SetRefPosition(o.Position);
            t.SetOffPosition(PointOf(pc.Spot, pc.Clock, center, hitbox));
            if (C.UseGradient) t.color = GradientColor.Get(C.Color1.ToVector4(), C.Color2.ToVector4()).ToUint();
            t.overlayText = $"{pc.Name.Split(' ')[0]} {pc.Clock}時";
        }
    }

    private void HidePoints() => Controller.GetRegisteredElements().Each(x => x.Value.Enabled = false);

    /// <summary>「自分」を BasePlayer から決め直す。Splatoon の Base Player Override (この人として見る) をその場で反映するため。</summary>
    private void RefreshMine()
    {
        var me = BasePlayer?.EntityId ?? 0;
        foreach (var pc in _partyDataList) pc.Mine = pc.EntityId == me;
    }

    /// <summary>毎フレーム、自分・全員の今の距離と近い順を取る (Debug の表と判定の確認用)。</summary>
    private void UpdateLive()
    {
        RefreshMine();
        var (center, _) = Kefka();
        foreach (var pc in _partyDataList)
        {
            pc.LiveRadius = pc.Object is { } o ? Distance2D(o.Position, center) : float.NaN;
            pc.LiveRank = -1;
        }
        var rank = 0;
        foreach (var pc in _partyDataList.Where(x => !float.IsNaN(x.LiveRadius)).OrderBy(x => x.LiveRadius))
            pc.LiveRank = rank++;
    }

    // ---- 座標 -----------------------------------------------------------------

    /// <summary>ケフカの位置とヒットボックス。見つからなければアリーナ中央と設定値。</summary>
    private (Vector3 Center, float Hitbox) Kefka()
    {
        if (_kefkaId.GetObject() is { } k) return (k.Position, k.HitboxRadius);
        return (ArenaCenter, C.FallbackHitbox);
    }

    /// <summary>今の配置の 5 点: タンク / ホーリー 3 つ (十字のタンクの次から時計回り) / 外周。TankFixed は 0 / 3 / 6 / 9 時。</summary>
    private IEnumerable<(Spot Spot, int Clock)> LayoutPoints()
    {
        yield return (Spot.Tank, _rotation);
        for (var i = 1; i <= 3; i++) yield return (Spot.Holy, (_rotation + 3 * i) % 12);
        yield return (Spot.Outer, OuterClock);
    }

    private float PointRadiusOf(Spot spot, float hitbox) => spot switch
    {
        Spot.Holy => hitbox + C.HolyOffset,
        Spot.Tank => hitbox + C.TankReach,
        Spot.Outer => C.OuterRadius,
        _ => float.NaN,
    };

    /// <summary>点。clock 時 (北 0 時、30° 刻み、時計回り) の方向に、種類ごとの半径。</summary>
    private Vector3 PointOf(Spot spot, int clock, Vector3 center, float hitbox) => center + DirOf(clock * 30f) * PointRadiusOf(spot, hitbox);

    /// <summary>北 0°、時計回りの角度 (度) の単位ベクトル。X が東、Z が南。</summary>
    private static Vector3 DirOf(float degrees)
    {
        var rad = degrees * MathF.PI / 180f;
        return new Vector3(MathF.Sin(rad), 0f, -MathF.Cos(rad));
    }

    private static float Distance2D(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

    /// <summary>北 0°、時計回りの角度 (0〜360)。</summary>
    private static float ClockAngle(Vector3 p, Vector3 center)
    {
        var deg = MathF.Atan2(p.X - center.X, -(p.Z - center.Z)) * 180f / MathF.PI;
        return (deg + 360f) % 360f;
    }

    private static string SpotText(Spot spot, int clock) => spot switch
    {
        Spot.Holy => $"{clock}時 ホーリー",
        Spot.Tank => $"{clock}時 タンク",
        Spot.Outer => "南 外周",
        _ => "-",
    };

    // ---- 設定 -----------------------------------------------------------------

    /// <summary>ユーザー向けは配置と優先順だけ。点の見た目は Registered elements の Edit、残りの調整は Debug の「設定」。</summary>
    private void DrawSettings()
    {
        DrawLayoutCombo("##Layout");
        if (C.Layout == LayoutMode.Rotate)
            ImGuiEx.TextWrapped("出発点を タンク 北、優先順の 1〜6 番目 = 北東 / 東 / 南東 / 南 / 南西 / 西 と仮定して十字を回す。遠隔・ヒーラーの移動を一番短くし、長く動くのは M2 / M1 (優先順の 3・4 番目) の枠に回す。例: 東 (R2 H2 M2) → タンク 10 時 / 1 時 / 4 時 / 7 時、西 (M1 H1 R1) → タンク 1 時 / 4 時 / 7 時 / 10 時。全員が同じ優先順・同じロールの割り当てを使うこと (各自の画面で別々に計算する)");
        ImGuiEx.TextWrapped("優先順 (非タンク 6 人)。上から見て、2 回目のホーリーの対象だけを拾った順に、十字のタンクの次から時計回り (タンク A 固定なら 3 時 → 6 時 → 9 時)。ロール指定なら、各ロールが誰かは Edit Roles の割り当てで決まる");
        C.Priority.Draw();
    }

    private void DrawLayoutCombo(string id)
    {
        ImGuiEx.Text("配置 (タンクと 2 回目の対象 3 人の十字の向き)");
        ImGui.SetNextItemWidth(360f);
        ImGuiEx.EnumCombo(id, ref C.Layout, new Dictionary<LayoutMode, string>
        {
            [LayoutMode.TankFixed] = "タンク A 固定: タンク 0 時 / 3 時 / 6 時 / 9 時",
            [LayoutMode.Rotate] = "既定: タンクも移動させて移動距離を均等化する (0〜11 時で十字を回す)",
        });
    }

    // ---- Debug: 設定 ----------------------------------------------------------

    private void DrawConfigTab()
    {
        ImGuiEx.TextWrapped("点の見た目 (半径・太さ・色・文字の大きさなど) は Registered elements の Point を Edit で変える。スクリプトが書くのは位置・表示・文字と、グラデーションが ON のときの色だけ。");
        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("ホーリーの点: ヒットボックスの縁から", ref C.HolyOffset, 0.05f, -2f, 3f, "%.2f");
        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("タンクの点: ヒットボックスの縁から", ref C.TankReach, 0.05f, 0f, 5f, "%.2f");
        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("外周の点: ケフカから", ref C.OuterRadius, 0.1f, 5f, 25f, "%.1f");
        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("ケフカが居ないときのヒットボックス", ref C.FallbackHitbox, 0.05f, 0f, 10f, "%.2f");
        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("移動の少ない向き: 仮定の出発点 (8 方向散開) の半径", ref C.SpreadRadius, 0.1f, 3f, 20f, "%.1f");
        ImGui.Checkbox("グラデーション (OFF なら Edit で決めた色のまま)", ref C.UseGradient);
        if (C.UseGradient)
        {
            DrawColor("グラデーション 1", ref C.Color1);
            ImGui.SameLine();
            DrawColor("グラデーション 2", ref C.Color2);
        }
        ImGui.Checkbox("文字を出す (担当と 2 回目までの秒)", ref C.ShowText);
        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("保険の時間切れ (秒、実機だけ。ふつうはイベントで終わる)", ref C.SafetySeconds, 0.5f, 10f, 30f, "%.1f");
        ImGui.Checkbox("Verbose log (dalamud.log にも出す)", ref C.VerboseLog);
        ImGui.Checkbox("Debug 表示 (全員の点・ホーリーの範囲 5.5・ヒットボックス)", ref C.DebugDraw);

        // 今のヒットボックスでの距離と余地。十字なので向きに関係なく、ホーリーどうしは 90°、ホーリーとタンクも 90° 離れる
        var (_, hb) = Kefka();
        var rh = hb + C.HolyOffset;
        var rt = hb + C.TankReach;
        var reach = HolyRange + PlayerHitbox;
        var adjacent = rh * MathF.Sqrt(2f);
        var toTank = MathF.Sqrt(rh * rh + rt * rt);
        ImGuiEx.Text($"ヒットボックス {hb:F2}{(_kefkaId.GetObject() == null ? " (設定値)" : "")} → ホーリーの点 {rh:F2} / タンク {rt:F2} / 外周 {C.OuterRadius:F1}");
        ImGuiEx.Text(adjacent > reach ? ImGuiColors(true) : ImGuiColors(false),
            $"隣のホーリーとの間 {adjacent:F2} (範囲 {reach:F1}、余地 {adjacent - reach:F2}) / タンクとの間 {toTank:F2} (余地 {toTank - reach:F2}) / タンクより {rt - rh:F2} 内側");
    }

    private static Vector4 ImGuiColors(bool ok) => ok ? new Vector4(0.5f, 1f, 0.5f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f);

    private static void DrawColor(string label, ref uint color)
    {
        var value = color.ToVector4();
        if (ImGui.ColorEdit4(label, ref value, ImGuiColorEditFlags.NoInputs)) color = value.ToUint();
    }

    // ---- Debug: 現在 ----------------------------------------------------------

    /// <summary>状態・ケフカ・5 つの点・8 人の表 (引かれる場所と判定結果のカラム付き)・このトライの判定。</summary>
    private void DrawNowTab()
    {
        RefreshMine();   // 終わった後でも、Base Player Override を切り替えたらその人の分を見せる
        var now = Environment.TickCount64;
        var (center, hitbox) = Kefka();
        ImGuiEx.Text($"自分: {BasePlayer?.Name.ToString() ?? "-"}{(Replay && global::Splatoon.Splatoon.BasePlayerOverride != "" ? " (Base Player Override)" : "")}");
        ImGuiEx.Text($"状態: {_state}{(_test ? " (テスト)" : "")}");
        ImGuiEx.Text($"47952 から {(_startMs == 0 || _state == State.None ? "-" : $"{(now - _startMs) / 1000f:F2}s")}" +
                     $" / 1 回目から {(_holy1Ms == 0 || _state == State.None ? "-" : $"{(now - _holy1Ms) / 1000f:F2}s")}");
        ImGuiEx.Text($"ケフカ: {NameOf(_kefkaId)} 0x{_kefkaId:X8} ({center.X:F2}, {center.Z:F2}) ヒットボックス {hitbox:F2}{(_kefkaId.GetObject() == null ? " (見つからない → 設定値)" : "")}");
        ImGuiEx.TextWrapped($"割り振り: {(_assignNote == "" ? "-" : _assignNote)}");
        ImGuiEx.TextWrapped($"配置: {C.Layout} / タンク {_rotation} 時{(_rotationNote == "" ? "" : $" / {_rotationNote}")}");
        foreach (var (spot, clock) in LayoutPoints())
        {
            var p = PointOf(spot, clock, center, hitbox);
            ImGuiEx.Text($"  {SpotText(spot, clock),-10} ({p.X:F2}, {p.Z:F2}) 半径 {PointRadiusOf(spot, hitbox):F2}");
        }
        if (BasePlayer is { } me)
        {
            var mine = _partyDataList.Find(x => x.Mine);
            var to = mine is { Spot: not Spot.None } ? Distance2D(me.Position, PointOf(mine.Spot, mine.Clock, center, hitbox)) : float.NaN;
            ImGuiEx.Text($"自分: ({me.Position.X:F2}, {me.Position.Z:F2}) ケフカから {Distance2D(me.Position, center):F2} / 点まで {to:F2}");
        }

        List<ImGuiEx.EzTableEntry> entries = [];
        foreach (var x in _partyDataList)
        {
            entries.Add(new("名前", true, () => ImGuiEx.Text(x.Mine ? $"{x.Name} (自分)" : x.Name)));
            entries.Add(new("ジョブ", () => ImGuiEx.Text($"{x.Job}{(x.IsTank ? " T" : "")}")));
            entries.Add(new("1回目", () => ImGuiEx.Text($"{(x.Holy1Target ? "中心" : "-")} {x.Holy1Hits}")));
            entries.Add(new("役", () => ImGuiEx.Text(x.Role.ToString())));
            entries.Add(new("担当", () => ImGuiEx.Text(SpotText(x.Spot, x.Clock))));
            entries.Add(new("順", () => ImGuiEx.Text(x.Order < 0 ? "-" : x.Order.ToString())));
            entries.Add(new("1回目の角度", () => ImGuiEx.Text(float.IsNaN(x.Holy1Angle) ? "-" : $"{x.Holy1Angle:F0}°")));
            entries.Add(new("1回目の距離", () => ImGuiEx.Text(float.IsNaN(x.Holy1Radius) ? "-" : $"{x.Holy1Radius:F2}")));
            entries.Add(new("今の距離", () => ImGuiEx.Text(float.IsNaN(x.LiveRadius) ? "-" : $"{x.LiveRadius:F2}")));
            entries.Add(new("近い順", () => ImGuiEx.Text(x.LiveRank < 0 ? "-" : $"{x.LiveRank + 1}{(x.LiveRank < 3 ? " ★" : "")}")));
            entries.Add(new("2回目", () => ImGuiEx.Text($"{(x.Holy2Target ? "中心" : "-")} {x.Holy2Hits}")));
            entries.Add(new("着弾時の距離", () => ImGuiEx.Text(float.IsNaN(x.Holy2Radius) ? "-" : $"{x.Holy2Radius:F2}")));
            entries.Add(new("引かれる場所", () => ImGuiEx.Text(PlaceText(x, center, hitbox))));
            entries.Add(new("判定結果", () => { var (ok, text) = PersonJudge(x); if (ok is { } b) ImGuiEx.Text(ImGuiColors(b), text); else ImGuiEx.Text(text); }));
        }
        ImGuiEx.EzTable(entries);

        // このトライの判定 (1 回目・2 回目のオーケストラ)
        foreach (var j in Enumerable.Reverse(_judges))
        {
            ImGui.Separator();
            ImGuiEx.Text($"{j.Label}  (着弾 {j.Time})");
            ImGuiEx.Text(ImGuiColors(j.PlannedMatch), $"予定どおり (割り振りの 3 人に飛んだ): {(j.PlannedMatch ? "Y" : "N")}");
            ImGuiEx.Text(ImGuiColors(j.NearestMatch), $"仮説どおり (着弾時に近い 3 人に飛んだ): {(j.NearestMatch ? "Y" : "N")}");
            ImGuiEx.TextWrapped($"近い順 (着弾時): {j.Nearest}");
        }
    }

    /// <summary>その人の点が引かれる座標。担当が無ければ "-"。</summary>
    private string PlaceText(PartyData x, Vector3 center, float hitbox)
    {
        if (x.Spot == Spot.None) return "-";
        var p = PointOf(x.Spot, x.Clock, center, hitbox);
        return $"({p.X:F1}, {p.Z:F1})";
    }

    /// <summary>
    /// その人についての 2 回目のホーリーの答え。null = まだ来ていない。
    /// 2 回目の担当なら「中心にされたか」、それ以外なら「当たっていないか」で ○ / ×。
    /// </summary>
    private static (bool? Ok, string Text) PersonJudge(PartyData x)
    {
        if (float.IsNaN(x.Holy2Radius)) return (null, "-");
        var hit = x.Holy2Target ? "中心" : x.Holy2Hits > 0 ? $"巻き込み×{x.Holy2Hits}" : "当たらず";
        var ok = x.Spot == Spot.Holy ? x.Holy2Target : x.Holy2Hits == 0;
        return (ok, $"{(ok ? "○" : "×")} {hit}");
    }

    // ---- Debug: テスト --------------------------------------------------------

    /// <summary>1 回目の対象を手で選び、本物と同じ Resolve / ShowPoints で点を出す。今のターゲットをケフカの代わりにする。</summary>
    private void DrawTestTab()
    {
        ImGuiEx.TextWrapped("1 回目のホーリーの対象を選んで「流す」。今のターゲットをケフカの代わりにする (無ければアリーナ中央とヒットボックスの設定値)。2 回目は来ないので時間切れで消える。");
        var party = FakeParty.Get().ToList();
        foreach (var pc in party)
        {
            var on = _testHoly1.Contains(pc.EntityId);
            if (ImGui.Checkbox($"{pc.Name} ({(Job)pc.ClassJob.RowId})##{pc.EntityId}", ref on))
            {
                if (on) _testHoly1.Add(pc.EntityId);
                else _testHoly1.Remove(pc.EntityId);
            }
        }
        if (ImGui.Button("流す"))
        {
            var target = ECommons.DalamudServices.Svc.Targets.Target;
            Begin(target?.EntityId ?? 0, true);
            foreach (var pc in _partyDataList) pc.Holy1Target = _testHoly1.Contains(pc.EntityId);
            _holy1Ms = Environment.TickCount64;
            Resolve();
            _state = State.Guide;
        }
        ImGui.SameLine();
        if (ImGui.Button("止める") && _state != State.None) Finish("テストを止めた");
    }

    // ---- Debug: 組み合わせ ----------------------------------------------------

    /// <summary>
    /// 2 回目の対象 3 人の組み合わせ (優先順の 6 人から 3 人 = 20 通り) ごとの割り振りを、本番と同じ計算 (ComputeRotation) で並べる。
    /// 「流す」は、その 3 人が 2 回目の対象 (残りの非タンクが 1 回目を受けた) として、本物と同じ Resolve / ShowPoints で点を出す。
    /// </summary>
    private void DrawComboTab()
    {
        DrawLayoutCombo("##LayoutCombo");
        var (_, hitbox) = Kefka();
        var players = C.Priority.GetPlayers(_ => true);   // null = 優先順がパーティと合わない
        var entries = C.Priority.PriorityLists.FirstOrDefault()?.List;
        ImGuiEx.TextWrapped($"ヒットボックス {hitbox:F2}{(_kefkaId.GetObject() == null ? " (設定値)" : "")}。仮定の出発点はタンク 北、優先順の 1〜6 番目 = 北東 / 東 / 南東 / 南 / 南西 / 西。" +
                            "括弧は仮定の位置から動く量 (時間分)、2 時間分以上は赤。「流す」は今のターゲットをケフカの代わりにする (無ければアリーナ中央と設定値)");
        if (players is not { Count: 6 })
            ImGuiEx.Text(ImGuiColors(false), "優先順がパーティと合わないので「流す」は使えない (表は優先順の枠の名前で計算する)");

        string Label(int i)
        {
            var slot = entries == null || i >= entries.Count ? $"#{i + 1}"
                : entries[i].Role != RolePosition.Not_Selected ? entries[i].Role.ToString() : entries[i].GetNameAndJob();
            var name = players is { Count: 6 } ? players[i].Name.Split(' ')[0] : null;
            return name == null ? slot : $"{name}({slot})";
        }

        List<ImGuiEx.EzTableEntry> rows = [];
        var n = 0;
        for (var a = 0; a < 6; a++)
        for (var b = a + 1; b < 6; b++)
        for (var c = b + 1; c < 6; c++)
        {
            var no = ++n;
            var idx = new[] { a, b, c };
            var rot = C.Layout == LayoutMode.Rotate ? ComputeRotation(idx, hitbox, out _) : 0;
            var tag = a == 0 && b == 1 && c == 2 ? " 東" : a == 3 && b == 4 && c == 5 ? " 西" : "";
            rows.Add(new("#", () => ImGuiEx.Text($"{no}{tag}")));
            rows.Add(new("対象 3 人", () => ImGuiEx.Text(string.Join("・", idx.Select(Label)))));
            rows.Add(new("タンク", () => DrawMove($"{rot}時", ClockHours(0f, rot * 30f))));
            for (var i = 0; i < 3; i++)
            {
                var k = i;
                var clock = (rot + 3 * (k + 1)) % 12;
                rows.Add(new($"{k + 1}人目", () => DrawMove($"{Label(idx[k])} {clock}時", ClockHours(StartAngle(idx[k]), clock * 30f))));
            }
            rows.Add(new("テスト", () =>
            {
                ImGui.BeginDisabled(players is not { Count: 6 });
                if (ImGui.SmallButton($"流す##combo{no}")) RunComboTest(idx, players!);
                ImGui.EndDisabled();
            }));
        }
        ImGuiEx.EzTable(rows);
        ImGui.BeginDisabled(_state == State.None);
        if (ImGui.Button("止める##combo")) Finish("組み合わせのテストを止めた");
        ImGui.EndDisabled();
    }

    private static void DrawMove(string text, float hours)
    {
        var label = $"{text} ({hours:F1}h)";
        if (hours >= 2f) ImGuiEx.Text(ImGuiColors(false), label);
        else ImGuiEx.Text(label);
    }

    /// <summary>組み合わせの「流す」。holy2Index の 3 人を 2 回目の対象、残りの非タンクを 1 回目の中心にして、本番と同じ道で点を出す。</summary>
    private void RunComboTest(int[] holy2Index, List<ECommons.PartyFunctions.UniversalPartyMember> players)
    {
        var target = ECommons.DalamudServices.Svc.Targets.Target;
        Begin(target?.EntityId ?? 0, true);
        var holy2 = holy2Index.Select(i => players[i].IGameObject?.EntityId ?? 0).ToHashSet();
        foreach (var pc in _partyDataList) pc.Holy1Target = !pc.IsTank && !holy2.Contains(pc.EntityId);
        _holy1Ms = Environment.TickCount64;
        Log($"組み合わせのテスト: 2 回目の対象 {string.Join(", ", holy2Index.Select(i => players[i].Name))}");
        Resolve();
        _state = State.Guide;
    }

    // ---- Debug: 履歴 ----------------------------------------------------------

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

    private static string NameOf(uint entityId) => entityId.GetObject()?.Name.ToString() ?? $"0x{entityId:X8}";

    /// <summary>リプレイ再生中か。</summary>
    private static bool Replay => ECommons.DalamudServices.Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.DutyRecorderPlayback];

    /// <summary>判断を履歴に残す。VerboseLog なら dalamud.log にも出す。</summary>
    private void Log(string message)
    {
        _history.Add($"{DateTime.Now:HH:mm:ss.fff} [{_state}] {message}");
        if (_history.Count > HistoryMax) _history.RemoveRange(0, _history.Count - HistoryMax);
        if (C.VerboseLog) PluginLog.Information($"[P5OH {_state}] {message}");
    }

    #endregion
}
