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
/// 立ち位置 (アリーナの北が 12 時。ケフカは北のタンクを向いている):
///   2 回目の対象 (1 回目を受けていない非タンク 3 人)
///       優先順 (6 人、非タンク) の順に 3 時 / 6 時 / 9 時。距離はヒットボックス + HolyOffset
///   1 回目を受けた非タンク 3 人   南の外周 (ケフカから OuterRadius)
///   タンク 2 人                  北、ヒットボックス + TankReach (ぎりぎり殴れる位置)
/// ケフカの位置とヒットボックスは毎フレーム実物から取る (ヒットボックスは計算式だと 1.5 × 3 = 4.5)。
/// 8 人全員の担当を解いてから、自分の点だけを描く。
///
/// 優先順が使えない / 2 回目の対象が 3 人にならないときは、2 回目の対象の点を出さない (履歴に理由を残す)。
/// 優先順の並びが 1 回目の時点の位置 (北から時計回り) と食い違ったら、履歴に警告を残す。
///
/// 2 回目のホーリーが来たら、実際に当たった人・その瞬間の距離・近い 3 人を記録して「判定」に残す。
/// 仮説 (近い 3 人に飛ぶ) と予定 (割り振り) の両方と突き合わせる。
///
/// 設定画面の Debug 欄は 4 タブ:
///   現在   状態、ケフカ、点の座標、8 人の表 (役・担当・1 回目の角度と距離・今の距離と近い順)
///   判定   直近の 2 回目のホーリーの答え合わせ
///   テスト 1 回目の対象を手で選んで、本物と同じ道で点を出す (今のターゲットをケフカの代わりにする)
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
        CollectHoly1,  // 1 回目の 3 発をまとめている
        Guide,         // 点を出している。2 回目のホーリー待ち
        CollectHoly2,  // 2 回目の着弾をまとめている (判定用)
    }

    private enum Role { None = 0, Holy2, Holy1, Tank }

    private enum Slot { None = 0, East, South, West, North, SouthOuter }

    private sealed class PartyData
    {
        public uint EntityId;
        public string Name = "";
        public Job Job;
        public bool IsTank;
        public bool Mine;

        public bool Holy1Target;      // 1 回目のホーリーの中心にされた
        public int Holy1Hits;         // 1 回目のホーリーに当たった数 (巻き込み含む)
        public float Holy1Angle = float.NaN;    // 1 回目の時点の角度 (北 0°、時計回り)
        public float Holy1Radius = float.NaN;   // 1 回目の時点のケフカからの距離

        public Role Role;
        public Slot Slot;
        public int Order = -1;        // 2 回目の対象の中の順番 (0 = 3 時)

        public bool Holy2Target;
        public int Holy2Hits;
        public float Holy2Radius = float.NaN;   // 2 回目の着弾の瞬間のケフカからの距離

        public float LiveRadius = float.NaN;    // 今のケフカからの距離
        public int LiveRank = -1;               // 今の近い順 (0 始まり)

        public IPlayerCharacter? Object => EntityId.GetObject() as IPlayerCharacter;
    }

    private sealed class Judge
    {
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
    public class Config
    {
        public PriorityData6 Priority = new()
        {
            Name = "P5 Orchestra Holy",
            Description = "非タンク 6 人。2 回目のホーリーの 3 人を、この順に 3 時 / 6 時 / 9 時へ割り振る",
        };
        public float HolyOffset = 0.5f;       // 2 回目の対象の点: ヒットボックスの縁から外へ
        public float TankReach = 3.0f;        // タンクの点: ヒットボックスの縁から外へ (近接は縁から 3)
        public float OuterRadius = 19f;       // 1 回目を受けた人の点: ケフカから (フィールドは半径 20 と推定)
        public float FallbackHitbox = 4.5f;   // ケフカが見つからないときのヒットボックス
        public float PointRadius = 0.6f;      // 点の円の半径
        public float Thickness = 15f;
        public uint Color1 = 0xC8FFFF00;      // グラデーション (ABGR)。2 色を同じにすると単色
        public uint Color2 = 0xC8FF00FF;
        public bool ShowText = true;
        public float TimeoutSeconds = 12f;    // 47952 からこれだけ経ったら畳む
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
    private const uint OrchestraCast = 47952;   // 狂気のオーケストラ (詠唱 4.7 秒)
    private const uint HolyId = 47956;          // ホーリー
    private const float HolyRange = 5f;         // ホーリーの円 (Lumina の EffectRange)
    private const float PlayerHitbox = 0.5f;
    private const long CollectMs = 300;          // 同じ回の着弾をまとめる時間
    private const long Holy2MinGapMs = 1500;     // 1 回目からこれより後のホーリーを 2 回目とみなす
    private const long ExpectedHoly2Ms = 3170;   // 1 回目 → 2 回目 (クリアログ 3.16〜3.17 秒、10/18 は 3.18 秒)
    private const int HistoryMax = 300;
    private static readonly Vector3 ArenaCenter = new(100f, 0f, 100f);

    private const string PointElement = "Point";
    private static readonly Slot[] DebugSlots = [Slot.East, Slot.South, Slot.West, Slot.North, Slot.SouthOuter];
    private static readonly Slot[] Holy2Slots = [Slot.East, Slot.South, Slot.West];
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
    private State _state = State.None;
    private readonly List<PartyData> _partyDataList = [];
    private uint _kefkaId;
    private long _startMs;
    private long _holy1Ms;
    private long _holy2Ms;
    private bool _test;
    private string _assignNote = "";            // 割り振りの根拠 / 失敗理由
    private Judge? _lastJudge;
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
        // Debug 用。全員の点 / 2 回目の点ごとのホーリーの範囲 / ケフカのヒットボックス
        foreach (var slot in DebugSlots)
            Controller.RegisterElement($"Dbg{slot}", new Element(0)
            {
                Enabled = false,
                radius = 0.4f,
                thicc = 2f,
                color = 0xC8FFFFFF,
                overlayTextColor = 0xFFFFFFFF,
                overlayVOffset = 1.5f,
            });
        foreach (var slot in Holy2Slots)
            Controller.RegisterElement($"DbgReach{slot}", new Element(0)
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
    }

    public override void OnStartingCast(uint source, uint castId)
    {
        if (castId == OrchestraCast) Begin(source, false);
    }

    public override void OnActionEffectEvent(ActionEffectSet set)
    {
        if (set.Action?.RowId != HolyId || _state == State.None || _test) return;
        var now = Environment.TickCount64;
        switch (_state)
        {
            case State.WaitHoly1:
                _holy1Ms = now;
                _state = State.CollectHoly1;
                RecordHoly1(set);
                break;
            case State.CollectHoly1:
                RecordHoly1(set);
                break;
            case State.Guide when now - _holy1Ms >= Holy2MinGapMs:
                _holy2Ms = now;
                SnapshotHoly2Radius();
                _state = State.CollectHoly2;
                HidePoints();   // 判定はもう済んだので、消す
                RecordHoly2(set);
                break;
            case State.CollectHoly2:
                RecordHoly2(set);
                break;
        }
    }

    public override void OnUpdate()
    {
        if (_state == State.None) return;
        var now = Environment.TickCount64;
        if (now - _startMs > C.TimeoutSeconds * 1000f)
        {
            Finish("時間切れ");
            return;
        }
        UpdateLive();
        if (_state == State.CollectHoly1 && now - _holy1Ms >= CollectMs)
        {
            Resolve();
            _state = State.Guide;
        }
        if (_state == State.CollectHoly2 && now - _holy2Ms >= CollectMs)
        {
            JudgeHoly2();
            Finish("2 回目のホーリー");
            return;
        }
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
        _holy2Ms = 0;
        _assignNote = "";
        Log("戦闘開始: 初期化");
    }

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
            ("現在", DrawNowTab, null, false),
            ("判定", DrawJudgeTab, null, false),
            ("テスト", DrawTestTab, null, false),
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
        _holy2Ms = 0;
        _test = test;
        _assignNote = "";
        BuildParty();
        _state = State.WaitHoly1;
        Log($"開始{(test ? " (テスト)" : "")}: ケフカ {NameOf(kefkaId)} 0x{kefkaId:X8} / パーティ {_partyDataList.Count} 人 / 自分 {_partyDataList.Find(x => x.Mine)?.Name ?? "居ない"}");
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
            pc.Slot = pc.Role switch { Role.Tank => Slot.North, Role.Holy1 => Slot.SouthOuter, _ => Slot.None };
            pc.Order = -1;
        }

        var tanks = _partyDataList.Count(x => x.Role == Role.Tank);
        var holy1 = _partyDataList.Count(x => x.Role == Role.Holy1);
        if (tanks != 2) Log($"警告: タンクが {tanks} 人");
        if (holy1 != 3) Log($"警告: 1 回目を受けた非タンクが {holy1} 人 (中心: {string.Join(", ", _partyDataList.Where(x => x.Holy1Target).Select(x => x.Name))})");

        _assignNote = AssignHoly2();
        Log($"割り振り: {_assignNote} / ケフカ ({center.X:F2}, {center.Z:F2}) ヒットボックス {hitbox:F2}");
        foreach (var pc in _partyDataList)
            Log($"  {pc.Name} {pc.Job} {pc.Role} {SlotText(pc.Slot)} 角度 {pc.Holy1Angle:F0}° 距離 {pc.Holy1Radius:F1}{(pc.Mine ? " (自分)" : "")}");
    }

    /// <summary>2 回目の対象 3 人を優先順で 3 時 / 6 時 / 9 時に。失敗したら理由を返して点は出さない。</summary>
    private string AssignHoly2()
    {
        var candidates = _partyDataList.Where(x => x.Role == Role.Holy2).ToList();
        if (candidates.Count != 3) return $"失敗: 2 回目の対象が {candidates.Count} 人";

        var ordered = C.Priority.GetPlayers(x => x.IGameObject is { } o && candidates.Any(c => c.EntityId == o.EntityId));
        if (ordered == null) return "失敗: 優先順が設定されていない / パーティと合わない";
        var list = ordered.Select(x => candidates.Find(c => c.EntityId == x.IGameObject?.EntityId)).OfType<PartyData>().Distinct().ToList();
        if (list.Count != 3)
            return $"失敗: 優先順で 3 人が並ばない ({list.Count} 人: {string.Join(", ", list.Select(x => x.Name))} / 対象: {string.Join(", ", candidates.Select(x => x.Name))})";

        for (var i = 0; i < 3; i++)
        {
            list[i].Order = i;
            list[i].Slot = Holy2Slots[i];
        }

        // 1 回目の時点の位置を北から時計回りに並べた順と比べる (クリアログ 3 本はこの順だった)
        var byAngle = candidates.Where(x => !float.IsNaN(x.Holy1Angle)).OrderBy(x => x.Holy1Angle).ToList();
        var note = $"優先順 {string.Join(" → ", list.Select(x => x.Name))}";
        if (byAngle.Count == 3 && !byAngle.SequenceEqual(list))
        {
            var msg = $"警告: 優先順と位置の時計回りが食い違い (位置: {string.Join(" → ", byAngle.Select(x => $"{x.Name} {x.Holy1Angle:F0}°"))})";
            Log(msg);
            note += " / " + msg;
        }
        return note;
    }

    private void JudgeHoly2()
    {
        var planned = _partyDataList.Where(x => x.Role == Role.Holy2 && x.Slot != Slot.None).ToList();
        var targets = _partyDataList.Where(x => x.Holy2Target).ToList();
        var nearest = _partyDataList.Where(x => !float.IsNaN(x.Holy2Radius)).OrderBy(x => x.Holy2Radius).Take(3).ToList();
        var mine = _partyDataList.Find(x => x.Mine);
        var (_, hitbox) = Kefka();
        _lastJudge = new Judge
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Planned = string.Join(", ", planned.Select(x => $"{x.Name} ({SlotText(x.Slot)})")),
            Targets = string.Join(", ", targets.Select(x => x.Name)),
            Hits = string.Join(", ", _partyDataList.Where(x => x.Holy2Hits > 0).Select(x => $"{x.Name}×{x.Holy2Hits}")),
            Nearest = string.Join(", ", nearest.Select(x => $"{x.Name} {x.Holy2Radius:F2}")),
            PlannedMatch = planned.Count == 3 && planned.All(x => x.Holy2Target) && targets.Count == 3,
            NearestMatch = nearest.Count == 3 && nearest.All(x => x.Holy2Target),
            Mine = mine == null ? "自分が居ない"
                : $"{mine.Role} {SlotText(mine.Slot)} / 着弾時の距離 {mine.Holy2Radius:F2} (点 {PointRadiusOf(mine.Slot, hitbox):F2}) / 当たり {mine.Holy2Hits}",
        };
        Log($"判定: 予定どおり={_lastJudge.PlannedMatch} 近い 3 人={_lastJudge.NearestMatch} / 中心 {_lastJudge.Targets} / 近い順 {_lastJudge.Nearest} / 自分 {_lastJudge.Mine}");
    }

    // ---- 表示 -----------------------------------------------------------------

    private void ShowPoints(long now)
    {
        var (center, hitbox) = Kefka();
        var left = (_holy1Ms + ExpectedHoly2Ms - now) / 1000f;

        var mine = _partyDataList.Find(x => x.Mine);
        if (mine != null && mine.Slot != Slot.None && Controller.TryGetElementByName(PointElement, out var e))
        {
            e.Enabled = true;
            e.SetRefPosition(PointOf(mine.Slot, center, hitbox));
            e.radius = C.PointRadius;
            e.thicc = C.Thickness;
            e.color = GradientColor.Get(C.Color1.ToVector4(), C.Color2.ToVector4()).ToUint();
            e.overlayText = C.ShowText ? $"{SlotText(mine.Slot)} {Math.Max(left, 0f):F1}" : "";
        }

        if (!C.DebugDraw) return;
        foreach (var slot in DebugSlots)
            if (Controller.TryGetElementByName($"Dbg{slot}", out var d))
            {
                d.Enabled = true;
                d.SetRefPosition(PointOf(slot, center, hitbox));
                var who = _partyDataList.Where(x => x.Slot == slot).Select(x => x.Name.Split(' ')[0]);
                d.overlayText = $"{SlotText(slot)}: {string.Join(",", who)}";
            }
        foreach (var slot in Holy2Slots)
            if (Controller.TryGetElementByName($"DbgReach{slot}", out var r))
            {
                r.Enabled = true;
                r.SetRefPosition(PointOf(slot, center, hitbox));
            }
        if (Controller.TryGetElementByName("DbgHitbox", out var h))
        {
            h.Enabled = true;
            h.radius = hitbox;
            h.SetRefPosition(center);
        }
    }

    private void HidePoints() => Controller.GetRegisteredElements().Each(x => x.Value.Enabled = false);

    /// <summary>毎フレーム、全員の今の距離と近い順を取る (Debug の表と判定の確認用)。</summary>
    private void UpdateLive()
    {
        var (center, _) = Kefka();
        foreach (var pc in _partyDataList)
            pc.LiveRadius = pc.Object is { } o ? Distance2D(o.Position, center) : float.NaN;
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

    private float PointRadiusOf(Slot slot, float hitbox) => slot switch
    {
        Slot.East or Slot.South or Slot.West => hitbox + C.HolyOffset,
        Slot.North => hitbox + C.TankReach,
        Slot.SouthOuter => C.OuterRadius,
        _ => float.NaN,
    };

    /// <summary>担当の点。X が東、Z が南。</summary>
    private Vector3 PointOf(Slot slot, Vector3 center, float hitbox)
    {
        var r = PointRadiusOf(slot, hitbox);
        var dir = slot switch
        {
            Slot.East => new Vector3(1f, 0f, 0f),
            Slot.South or Slot.SouthOuter => new Vector3(0f, 0f, 1f),
            Slot.West => new Vector3(-1f, 0f, 0f),
            Slot.North => new Vector3(0f, 0f, -1f),
            _ => Vector3.Zero,
        };
        return center + dir * r;
    }

    private static float Distance2D(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

    /// <summary>北 0°、時計回りの角度 (0〜360)。</summary>
    private static float ClockAngle(Vector3 p, Vector3 center)
    {
        var deg = MathF.Atan2(p.X - center.X, -(p.Z - center.Z)) * 180f / MathF.PI;
        return (deg + 360f) % 360f;
    }

    private static string SlotText(Slot slot) => slot switch
    {
        Slot.East => "3時 ホーリー",
        Slot.South => "6時 ホーリー",
        Slot.West => "9時 ホーリー",
        Slot.North => "北 タンク",
        Slot.SouthOuter => "南 外周",
        _ => "-",
    };

    // ---- 設定 -----------------------------------------------------------------

    private void DrawSettings()
    {
        ImGuiEx.Text("優先順 (非タンク 6 人。2 回目のホーリーの 3 人をこの順に 3 時 / 6 時 / 9 時)");
        C.Priority.Draw();
        ImGui.Separator();
        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("ホーリーの点: ヒットボックスの縁から", ref C.HolyOffset, 0.05f, -2f, 3f, "%.2f");
        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("タンクの点: ヒットボックスの縁から", ref C.TankReach, 0.05f, 0f, 5f, "%.2f");
        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("外周の点: ケフカから", ref C.OuterRadius, 0.1f, 5f, 25f, "%.1f");
        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("ケフカが居ないときのヒットボックス", ref C.FallbackHitbox, 0.05f, 0f, 10f, "%.2f");
        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("点の半径", ref C.PointRadius, 0.05f, 0.1f, 3f, "%.2f");
        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("太さ", ref C.Thickness, 0.5f, 1f, 30f, "%.1f");
        DrawColor("グラデーション 1", ref C.Color1);
        ImGui.SameLine();
        DrawColor("グラデーション 2", ref C.Color2);
        ImGui.Checkbox("文字を出す (担当と 2 回目までの秒)", ref C.ShowText);
        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("時間切れ (秒)", ref C.TimeoutSeconds, 0.5f, 5f, 30f, "%.1f");
        ImGui.Checkbox("Verbose log (dalamud.log にも出す)", ref C.VerboseLog);
        ImGui.Checkbox("Debug 表示 (全員の点・ホーリーの範囲 5.5・ヒットボックス)", ref C.DebugDraw);

        // 今のヒットボックスでの距離と余地。ホーリーどうし (3 時–6 時) とホーリー–タンク
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

    private void DrawNowTab()
    {
        var now = Environment.TickCount64;
        var (center, hitbox) = Kefka();
        ImGuiEx.Text($"状態: {_state}{(_test ? " (テスト)" : "")}");
        ImGuiEx.Text($"47952 から {(_startMs == 0 || _state == State.None ? "-" : $"{(now - _startMs) / 1000f:F2}s")}" +
                     $" / 1 回目から {(_holy1Ms == 0 || _state == State.None ? "-" : $"{(now - _holy1Ms) / 1000f:F2}s")}");
        ImGuiEx.Text($"ケフカ: {NameOf(_kefkaId)} 0x{_kefkaId:X8} ({center.X:F2}, {center.Z:F2}) ヒットボックス {hitbox:F2}{(_kefkaId.GetObject() == null ? " (見つからない → 設定値)" : "")}");
        ImGuiEx.TextWrapped($"割り振り: {(_assignNote == "" ? "-" : _assignNote)}");
        foreach (var slot in DebugSlots)
        {
            var p = PointOf(slot, center, hitbox);
            ImGuiEx.Text($"  {SlotText(slot),-10} ({p.X:F2}, {p.Z:F2}) 半径 {PointRadiusOf(slot, hitbox):F2}");
        }
        if (BasePlayer is { } me)
        {
            var mine = _partyDataList.Find(x => x.Mine);
            var to = mine is { Slot: not Slot.None } ? Distance2D(me.Position, PointOf(mine.Slot, center, hitbox)) : float.NaN;
            ImGuiEx.Text($"自分: ({me.Position.X:F2}, {me.Position.Z:F2}) ケフカから {Distance2D(me.Position, center):F2} / 点まで {to:F2}");
        }

        List<ImGuiEx.EzTableEntry> entries = [];
        foreach (var x in _partyDataList)
        {
            entries.Add(new("名前", true, () => ImGuiEx.Text(x.Mine ? $"{x.Name} (自分)" : x.Name)));
            entries.Add(new("ジョブ", () => ImGuiEx.Text($"{x.Job}{(x.IsTank ? " T" : "")}")));
            entries.Add(new("1回目", () => ImGuiEx.Text($"{(x.Holy1Target ? "中心" : "-")} {x.Holy1Hits}")));
            entries.Add(new("役", () => ImGuiEx.Text(x.Role.ToString())));
            entries.Add(new("担当", () => ImGuiEx.Text(SlotText(x.Slot))));
            entries.Add(new("順", () => ImGuiEx.Text(x.Order < 0 ? "-" : x.Order.ToString())));
            entries.Add(new("1回目の角度", () => ImGuiEx.Text(float.IsNaN(x.Holy1Angle) ? "-" : $"{x.Holy1Angle:F0}°")));
            entries.Add(new("1回目の距離", () => ImGuiEx.Text(float.IsNaN(x.Holy1Radius) ? "-" : $"{x.Holy1Radius:F2}")));
            entries.Add(new("今の距離", () => ImGuiEx.Text(float.IsNaN(x.LiveRadius) ? "-" : $"{x.LiveRadius:F2}")));
            entries.Add(new("近い順", () => ImGuiEx.Text(x.LiveRank < 0 ? "-" : $"{x.LiveRank + 1}{(x.LiveRank < 3 ? " ★" : "")}")));
            entries.Add(new("2回目", () => ImGuiEx.Text($"{(x.Holy2Target ? "中心" : "-")} {x.Holy2Hits}")));
            entries.Add(new("着弾時の距離", () => ImGuiEx.Text(float.IsNaN(x.Holy2Radius) ? "-" : $"{x.Holy2Radius:F2}")));
        }
        ImGuiEx.EzTable(entries);
    }

    // ---- Debug: 判定 ----------------------------------------------------------

    private void DrawJudgeTab()
    {
        if (_lastJudge is not { } j)
        {
            ImGuiEx.Text("まだ 2 回目のホーリーを見ていない");
            return;
        }
        ImGuiEx.Text($"時刻: {j.Time}");
        ImGuiEx.Text(ImGuiColors(j.PlannedMatch), $"予定どおり (割り振りの 3 人に飛んだ): {(j.PlannedMatch ? "Y" : "N")}");
        ImGuiEx.Text(ImGuiColors(j.NearestMatch), $"仮説どおり (着弾時に近い 3 人に飛んだ): {(j.NearestMatch ? "Y" : "N")}");
        ImGuiEx.TextWrapped($"予定: {j.Planned}");
        ImGuiEx.TextWrapped($"中心: {j.Targets}");
        ImGuiEx.TextWrapped($"当たり: {j.Hits}");
        ImGuiEx.TextWrapped($"近い順 (着弾時): {j.Nearest}");
        ImGuiEx.TextWrapped($"自分: {j.Mine}");
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

    /// <summary>判断を履歴に残す。VerboseLog なら dalamud.log にも出す。</summary>
    private void Log(string message)
    {
        _history.Add($"{DateTime.Now:HH:mm:ss.fff} [{_state}] {message}");
        if (_history.Count > HistoryMax) _history.RemoveRange(0, _history.Count - HistoryMax);
        if (C.VerboseLog) PluginLog.Information($"[P5OH {_state}] {message}");
    }

    #endregion
}
