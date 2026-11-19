using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.Hooks.ActionEffectTypes;
using ECommons.ImGuiMethods;
using Splatoon;
using Splatoon.SplatoonScripting;

namespace RedmoonsScripts.Duties.Dawntrail.Dancing_Mad;

/// <summary>
/// Dancing Mad (Ultimate) P3 どんどこ地団駄 (Stomp a mole) の立ち位置。
/// </summary>
/// <remarks>
/// Splatoon のレイアウト「[Select configuration] Dmad P3 Stomp a mole - dps (rel south)」と
/// 「… - support (rel north)」(グループ「絶妖星乱舞 - NXIV &amp; mirage(translate)」) の表示 (位置・色・線) を写したもの。
/// いつ何を出すかは、レイアウトの「マーカーの 5 秒の窓」ではなく、ギミックの順番で決める (v6)。
///
/// 順番 (ログ 3 本で確認):
///   ブリザガ (47887) の詠唱               → [A] ブリザガの位置 + [B] 予備の位置
///   地団駄 (47855) の詠唱 → 1 回目の頭割りのマーカー (com_share3t、1 人に付く) → そのロールが中央、反対のロールが塔
///     地団駄の詠唱 4 秒以内: 中央 = 両方の塔に「入らない」+ [C] 予備 / 塔 = [D] 塔 + [E] 予備
///     それ以降:             中央 = 両方の塔に「入らない」+ [G] 中央 / 塔 = [F] 塔 (線あり)
///   塔の地団駄 (47856) は 2 人ずつ 4 組、約 1.3 秒おき。1・2 組目が 1 回目の塔、3・4 組目が 2 回目の塔
///   1 回目の頭割り (47875) が着弾 → 役割を入れ替えて 2 回目 (2 回目のマーカーは反対のロールに付く)
///   2 回目の頭割りが着弾 → 終わり (何も出さない)
/// 役割が終わる時 (設定「数える時」で 結果 = OnActionEffectApplied (既定) か 発表 = OnActionEffectEvent を選ぶ):
///   中央 = 1 回目は頭割りと塔の 2 組目が両方終わった時、2 回目は頭割り (47875) が終わった時
///   塔   = 自分の組が終わった時 (結果なら自分の結果、発表なら自分が対象に入った発表)。
///          自分が入らなかった時 (死んでいる等) はその回の最後の組 (2 組目 / 4 組目) が終わった時
///          → 先に踏まれる D1・D3 は 1 組目で、後に踏まれる D2・D4 は 2 組目で次へ
///   組 = 発表 1 回 (対象 0〜2 人)。結果で数える時は、その発表の対象全員の結果が出たら組の終わり
///   (対象 0 人の組は発表で終わり、次の組の結果が先に来たら前の組も終わり扱い)
/// 位置はケフカ本体 (DataId 19504) の向きに対する相対。Left = ケフカの顔を見て左 (+X)。
/// DPS は南寄り (+Y 9)、タンク・ヒーラーは北寄り (-Y 9)。ロールはジョブから自動 (設定で固定もできる)。
///
/// v14: 結果 (既定) と発表を設定で切り替える。AE で動かす時は発表だと判定負けする。
///      結果の組は発表の対象人数で数える (1 人だけの組の立て直しに時間を使わない)。
/// v13: 発表 (OnActionEffectEvent) で数えた。結果は発表より塔で 0.19〜0.27 秒、頭割りで 0.45〜0.64 秒遅い (2026-11-19 のリプレイ)。
/// v12: 通常の流れはイベントだけ (人数 / 2)。組が 1 人だけ (死んでいる等) の時だけ、間隔 0.6 秒で 1 人分足して立て直す。
/// v11: 塔は当たった人数で数える (組 = 人数 / 2)。v10 の左右の判定はやめた。
/// v10: 1 回目の中央 → 塔は「頭割りの着弾」かつ「2 組目で担当方向の塔にダメージ」。地団駄が当たった人の位置で左右を判定して記録にも出す。
/// v9: 1 回目に中央の人が塔へ移るのは、頭割りの着弾ではなく塔の 2 組目がダメージを受けた時 (逆のパターンの DPS など)。
/// v8: 塔の保険は最後の組が全員分出てから (2 組目の 1 人目の結果で中央へ行き、自分より 70ms 早かった)。
///     円の基準を NameID 7131 から本体の DataId 19504 に (塔の位置のモグラ 19451 も基準になっていた)。
/// v7: 記録 (表示の切り替え・マーカー・塔・頭割り・NameID 7131 の一覧) を設定画面に出し、コピーできるようにした。
/// v6: 順番どおりに進む作りに書き直した (v5 まではレイアウトの条件を写していて、最後の頭割りの着弾を見ていなかった)。
/// v5: 敵の詠唱を見る所の NullReferenceException を直した。
/// v3: マーカーは 1 人にしか付かないので、ロールは「誰か 1 人」で見る。
///
/// 元のレイアウトを有効にしたままだと二重に描くので、レイアウトは切っておく。
/// </remarks>
internal class P3_Stomp_A_Mole : SplatoonScript
{
    #region types
    /********************************************************************/
    /* types                                                            */
    /********************************************************************/
    public enum Strat { Regular = 0, RoleStomps = 1 }
    public enum Side { Left = 0, Right = 1 }
    public enum RoleMode { Auto = 0, Dps = 1, Support = 2 }
    public enum CountBy { Applied = 0, Announce = 1 }   // 結果 (HP が減った時) / 発表 (ActionEffect)
    #endregion

    #region class
    /********************************************************************/
    /* class                                                            */
    /********************************************************************/
    public class Config : IEzConfig
    {
        public Strat Strat = Strat.Regular;   // レイアウトで選んでいた「Regular strat - Group 2/Right」
        public Side Side = Side.Right;
        public RoleMode Role = RoleMode.Auto;
        public CountBy CountBy = CountBy.Applied;
    }

    // 塔の 1 組 (発表 1 回)
    private sealed class Pair
    {
        public int Index;                          // 1〜4
        public int Expected;                       // 発表の対象人数
        public readonly HashSet<ulong> Got = [];   // 結果が出た対象
    }
    #endregion

    #region const
    /********************************************************************/
    /* const                                                            */
    /********************************************************************/
    private const uint BlizzardNameId = 6052;
    private const uint BlizzardCast = 47887;
    private const uint KefkaNameId = 7131;
    private const uint KefkaDataId = 19504;      // 表示の基準にする本体。NameID 7131 には塔のモグラ (19451) 8 体と補助 (9020) もいる
    private const uint StompCast = 47855;
    private const uint StompHit = 47856;    // 塔の 2 人ずつ 4 組
    private const uint StackHit = 47875;    // 頭割りの着弾
    private const float StompEarlySeconds = 4f;
    private const string StackVfx = "vfx/lockon/eff/com_share3t.avfx";
    private const long MechanicMaxMs = 25000;   // 地団駄の詠唱からこれを過ぎたら終わり扱い (保険)
    private const long BlizzardGapMs = 3000;    // ブリザガの詠唱が終わってから 1 回目のマーカーまで [A][B] を残す

    // 色・太さ・塗りはレイアウトの値
    private const uint ColMiddle = 3357671168, ColRelSpot = 3355508496, ColPreLeft = 3355508731, ColPreRight = 3355508706;
    private const uint ColNoTower = 3355443455, ColCastPre = 3355508496;
    private const uint ColTowerLeft = 3355508725, ColTowerRight = 3355506687, ColTowerLeftT = 3356884736, ColTowerRightT = 3357277952;
    #endregion

    #region public properties
    /********************************************************************/
    /* public properties                                                */
    /********************************************************************/
    public override HashSet<uint>? ValidTerritories => [1363];
    public override Metadata? Metadata => new(14, "Redmoon");
    #endregion

    #region private properties
    /********************************************************************/
    /* private properties                                               */
    /********************************************************************/
    private Config C => Controller.GetConfig<Config>();

    private long _stompStartMs;            // 地団駄 (47855) の詠唱が始まった時刻 (0 = ギミックの外)
    private long _blizzardSeenMs;          // ブリザガの詠唱を最後に見た時刻
    private bool? _firstMarkerOnDps;       // 1 回目のマーカーが DPS に付いたか (null = まだ)
    private int _stompPairs;               // 塔の地団駄 (47856) の終わった組 (1〜4、0 = まだ)
    private int _myStompPair;              // 自分の組 (1〜4、0 = まだ)
    private int _stacks;                   // 頭割り (47875) の終わった回数
    private int _announcedPairs;           // 塔の発表が来た回数
    private readonly Dictionary<uint, Pair> _pairs = [];     // 発表の通し番号 → 組
    private readonly HashSet<uint> _stackSeqs = [];           // 頭割りの発表の通し番号
    private readonly HashSet<uint> _stackCounted = [];        // 結果で数えた頭割りの通し番号 (2 人目以降を数えない)

    // 記録 (設定画面からコピーする。dalamud.log には頼らない)
    private readonly List<string> _log = [];
    private string _lastShownKey = "";
    private readonly List<string> _shownNow = [];
    #endregion

    #region public methods
    /********************************************************************/
    /* public methods                                                   */
    /********************************************************************/
    public override void OnSetup()
    {
        Controller.RegisterElement("A_Middle", Fixed(ColMiddle, tether: true));
        Controller.RegisterElement("G_Middle", Fixed(ColMiddle, tether: true));
        foreach (var dps in new[] { true, false })
        {
            var y = dps ? 9f : -9f;
            var r = dps ? "D" : "S";
            Controller.RegisterElement($"A_Rel_{r}", Rel(0f, y, 2f, 0.5f, ColRelSpot, 5f, 0.5f, tether: true));
            Controller.RegisterElement($"B_L_{r}", Rel(8f, y, 2f, 0.5f, ColPreLeft, 5f, 0.5f, tether: false));
            Controller.RegisterElement($"B_R_{r}", Rel(-8f, y, 2f, 0.5f, ColPreRight, 5f, 0.5f, tether: false));
            Controller.RegisterElement($"Pre_L_{r}", Rel(8f, y, 2f, 0.5f, ColCastPre, 5f, 0.5f, tether: true));   // [C] / [E]
            Controller.RegisterElement($"Pre_R_{r}", Rel(-8f, y, 2f, 0.5f, ColCastPre, 5f, 0.5f, tether: true));
        }
        Controller.RegisterElement("NoTower_L", Rel(10f, 0f, 5f, 0f, ColNoTower, 4f, 0.7f, tether: false));
        Controller.RegisterElement("NoTower_R", Rel(-10f, 0f, 5f, 0f, ColNoTower, 4f, 0.7f, tether: false));
        Controller.RegisterElement("D_L", Rel(10f, 0f, 4f, 1f, ColTowerLeft, 4f, 0.5f, tether: false));
        Controller.RegisterElement("D_R", Rel(-10f, 0f, 4f, 1f, ColTowerRight, 4f, 0.5f, tether: false));
        Controller.RegisterElement("F_L", Rel(10f, 0f, 4f, 1f, ColTowerLeftT, 4f, 0.5f, tether: true));
        Controller.RegisterElement("F_R", Rel(-10f, 0f, 4f, 1f, ColTowerRightT, 4f, 0.5f, tether: true));
    }

    public override void OnStartingCast(uint source, uint castId)
    {
        if (castId != StompCast) return;
        OnReset();
        _stompStartMs = Environment.TickCount64;
        Log($"地団駄 47855 の詠唱開始 (source {source:X8})");
    }

    // 1 回目のマーカーだけ使う (2 回目は反対のロールに付く)
    public override void OnVFXSpawn(uint target, string vfxPath)
    {
        if (vfxPath != StackVfx) return;
        var pc = target.GetObject() as IPlayerCharacter;
        Log($"頭割りのマーカー: {Who(target)} ({(pc == null ? "?" : IsDps(pc) ? "DPS" : "サポート")})"
            + (_stompStartMs == 0 ? " ※地団駄の詠唱の前なので使わない" : _firstMarkerOnDps != null ? " (2 回目以降)" : " → 1 回目"));
        if (_stompStartMs == 0 || _firstMarkerOnDps != null) return;
        if (pc != null) _firstMarkerOnDps = IsDps(pc);
    }

    // 技の発表 (ActionEffect)。組と対象はここで覚える。結果 (HP が減る) はこの 0.2〜0.6 秒後
    public override void OnActionEffectEvent(ActionEffectSet set)
    {
        if (_stompStartMs == 0) return;
        var id = set.Header.ActionID;
        if (id != StompHit && id != StackHit) return;
        var seq = set.Header.GlobalEffectCounter;
        var me = BasePlayer?.EntityId ?? 0;
        var mine = false;
        var who = new List<string>();
        foreach (var t in set.TargetEffects)
        {
            if (me != 0 && (uint)t.TargetID == me) mine = true;
            who.Add(Who(t.TargetID));
        }
        var announce = C.CountBy == CountBy.Announce;
        if (id == StompHit)
        {
            var p = new Pair { Index = ++_announcedPairs, Expected = set.TargetEffects.Length };
            _pairs[seq] = p;
            Log($"塔の地団駄 47856 組 {p.Index} の発表: {string.Join(", ", who)}{(mine ? " ← 自分" : "")}");
            if (announce && mine && _myStompPair == 0) _myStompPair = p.Index;
            if (announce) PairDone(p.Index, "発表");
            else if (p.Expected == 0) PairDone(p.Index, "対象 0 人");
        }
        else
        {
            _stackSeqs.Add(seq);
            Log($"頭割り 47875 の発表: {string.Join(", ", who)}");
            if (announce) StackDone("発表");
        }
    }

    // 技の結果 (HP が実際に減った時)。発表で覚えた通し番号で組・頭割りを引く
    public override void OnActionEffectApplied(ActionEffectSet set, int targetIndex, ActionEffectResult result)
    {
        if (_stompStartMs == 0 || result.IsSource) return;
        var seq = result.Sequence;
        var tower = _pairs.TryGetValue(seq, out var p);
        if (!tower && !_stackSeqs.Contains(seq)) return;
        var mine = BasePlayer != null && result.TargetId == BasePlayer.EntityId;
        Log($"{(tower ? $"塔の地団駄 47856 組 {p!.Index}" : "頭割り 47875")} の結果: {Who(result.TargetId)}{(mine ? " ← 自分" : "")} (+{result.DelayMs}ms)");
        if (C.CountBy != CountBy.Applied) return;
        if (!tower)
        {
            if (_stackCounted.Add(seq)) StackDone("結果");   // 2 人目以降は数えない
            return;
        }
        p!.Got.Add(result.TargetId);
        if (mine && _myStompPair == 0) _myStompPair = p.Index;
        if (p.Index - 1 > _stompPairs) PairDone(p.Index - 1, $"組 {p.Index} の結果が先に来た");
        if (p.Got.Count >= p.Expected) PairDone(p.Index, "結果が全員分");
    }

    public override void OnReset()
    {
        _stompStartMs = 0;
        _firstMarkerOnDps = null;
        _stompPairs = _myStompPair = _stacks = _announcedPairs = 0;
        _pairs.Clear();
        _stackSeqs.Clear();
        _stackCounted.Clear();
    }

    public override void OnUpdate()
    {
        _shownNow.Clear();
        UpdateCore();
        var key = string.Join(" ", _shownNow);
        if (key != _lastShownKey)
        {
            _lastShownKey = key;
            Log($"表示: [{(key == "" ? "なし" : key)}] / 塔 {_stompPairs} 組 (自分の組 {_myStompPair}) 頭割り {_stacks} / {KefkaList()}");
        }
    }

    private void UpdateCore()
    {
        HideAll();
        if (BasePlayer == null) return;
        var now = Environment.TickCount64;
        if (_stompStartMs != 0 && now - _stompStartMs > MechanicMaxMs) OnReset();

        var dps = IsDpsSide();
        var r = dps ? "D" : "S";
        var lr = C.Side == Side.Left ? "L" : "R";

        // ブリザガ: 詠唱中と、終わってから 1 回目のマーカーが付くまで (レイアウトはここで一瞬消えていた)
        var blizzard = IsCasting(BlizzardNameId, BlizzardCast, null);
        if (blizzard) _blizzardSeenMs = now;
        if (blizzard || (_stompStartMs != 0 && _firstMarkerOnDps == null && now - _blizzardSeenMs <= BlizzardGapMs))
        {
            Show(C.Strat == Strat.Regular ? "A_Middle" : $"A_Rel_{r}");
            Show($"B_{lr}_{r}");
            return;
        }
        if (_stompStartMs == 0 || _firstMarkerOnDps == null) return;

        var middleFirst = _firstMarkerOnDps == dps;   // 1 回目は自分のロールが中央か
        // 1 回目の中央は、頭割りと塔の 2 組目の発表が両方来たら次 (自分の塔) へ
        var round1Done = middleFirst ? _stacks >= 1 && _stompPairs >= 2 : TowerDone(1);
        if (!round1Done)
        {
            var early = IsCasting(KefkaNameId, StompCast, StompEarlySeconds);
            if (middleFirst)
            {
                Show("NoTower_L"); Show("NoTower_R"); Show(early ? $"Pre_{lr}_{r}" : "G_Middle");
            }
            else
            {
                if (early) { Show($"D_{lr}"); Show($"Pre_{lr}_{r}"); }
                else Show($"F_{lr}");
            }
            return;
        }

        // 2 回目は役割が入れ替わる
        var round2Done = middleFirst ? TowerDone(2) : _stacks >= 2;
        if (round2Done) return;
        if (middleFirst) Show($"F_{lr}");
        else { Show("NoTower_L"); Show("NoTower_R"); Show("G_Middle"); }
    }

    public override void OnSettingsDraw()
    {
        ImGuiEx.EnumCombo("作戦##strat", ref C.Strat);
        ImGuiEx.EnumCombo("ケフカの顔を見て左右##side", ref C.Side);
        ImGuiEx.EnumCombo("ロール (Auto = ジョブから)##role", ref C.Role);
        ImGuiEx.Text("塔・頭割りを数える時:");
        ImGui.SameLine();
        if (ImGui.RadioButton("結果 (HP が減った時、AE 向け)##countApplied", C.CountBy == CountBy.Applied)) C.CountBy = CountBy.Applied;
        ImGui.SameLine();
        if (ImGui.RadioButton("発表 (0.2〜0.5 秒早い)##countAnnounce", C.CountBy == CountBy.Announce)) C.CountBy = CountBy.Announce;
        ImGuiEx.Text($"今の判定: {(IsDpsSide() ? "DPS (南寄り)" : "タンク・ヒーラー (北寄り)")}");
        ImGuiEx.Text($"ギミック中: {_stompStartMs != 0} / 1 回目のマーカー: {(_firstMarkerOnDps == null ? "-" : _firstMarkerOnDps.Value ? "DPS" : "サポート")}"
                     + $" / 塔 {_stompPairs} 組 (自分の組 {_myStompPair}) / 頭割り {_stacks}");
        ImGui.Separator();
        if (ImGui.Button("記録をコピー")) ImGui.SetClipboardText(string.Join(Environment.NewLine, _log));
        ImGui.SameLine();
        if (ImGui.Button("記録を消す")) _log.Clear();
        ImGuiEx.Text($"記録 {_log.Count} 行 (地団駄の詠唱で消さない。最大 400 行)");
        foreach (var line in _log.TakeLast(60)) ImGuiEx.Text(line);
    }
    #endregion

    #region private methods
    /********************************************************************/
    /* private methods                                                  */
    /********************************************************************/
    private void Log(string text)
    {
        var t = _stompStartMs == 0 ? "" : $"+{(Environment.TickCount64 - _stompStartMs) / 1000.0:0.00}s ";
        _log.Add($"{DateTime.Now:HH:mm:ss.fff} {t}{text}");
        if (_log.Count > 400) _log.RemoveAt(0);
    }

    private static string Who(ulong id)
    {
        var o = Svc.Objects.SearchById(id);
        return o == null ? $"{id:X8}" : o is IPlayerCharacter pc ? $"{pc.Name}({pc.ClassJob.Value.Abbreviation})" : $"{o.Name}#{o.BaseId}";
    }

    // 表示の基準になる NameID 7131 の敵 (分身が基準になっていないか確かめる用)
    private static string KefkaList()
    {
        var list = new List<string>();
        foreach (var o in Svc.Objects)
        {
            try
            {
                if (o is IBattleNpc x && x.NameId == KefkaNameId && x.BaseId != 9020)   // 補助 (9020) は数が多いので出さない
                    list.Add($"7131#{x.BaseId}({x.Position.X:0.0},{x.Position.Z:0.0} r{x.Rotation:0.00} vis{x.IsTargetable})");
            }
            catch (NullReferenceException) { }
        }
        return list.Count == 0 ? "7131 なし" : string.Join(" ", list);
    }

    private void PairDone(int index, string why)
    {
        if (index <= _stompPairs) return;
        _stompPairs = index;
        Log($"塔 組 {index} 終わり ({why})");
    }

    private void StackDone(string why)
    {
        _stacks++;
        Log($"頭割り #{_stacks} 終わり ({why})");
    }

    // その回 (1 = 1・2 組目、2 = 3・4 組目) の塔が自分の分まで終わったか。
    // 自分が対象に入らなかった時は、その回の最後の組の発表が来た時
    private bool TowerDone(int round)
    {
        var first = round * 2 - 1;
        var last = round * 2;
        if (_myStompPair >= first && _myStompPair <= last) return true;
        return _stompPairs >= last;
    }

    private static Element Fixed(uint color, bool tether) => new(0)
    {
        refX = 100f, refY = 100f, radius = 2f, Donut = 0.5f, color = color, Filled = true, fillIntensity = 0.5f, thicc = 5f, tether = tether,
    };

    // ケフカ本体 (DataId 19504) の向きに対する相対位置。
    // レイアウトは NameID 7131 で、塔の位置のモグラ (19451) を基準にした円も出ていた (塔の円が中央あたりに見える)
    private static Element Rel(float x, float y, float radius, float donut, uint color, float thicc, float fill, bool tether) => new(1)
    {
        refActorDataID = KefkaDataId, refActorComparisonType = 3, includeRotation = true,
        offX = x, offY = y, radius = radius, Donut = donut, color = color, Filled = true, fillIntensity = fill, thicc = thicc, tether = tether,
    };

    private void Show(string name)
    {
        _shownNow.Add(name);
        if (Controller.TryGetElementByName(name, out var e)) e.Enabled = true;
    }

    private void HideAll()
    {
        foreach (var e in Controller.GetRegisteredElements()) e.Value.Enabled = false;
    }

    // NameID の敵がその技を詠唱中か。maxElapsed があれば詠唱開始からその秒数以内だけ。
    // 消えかけの敵で NullReferenceException が出ることがあったので 1 体ずつ守る
    private static bool IsCasting(uint nameId, uint castId, float? maxElapsed)
    {
        foreach (var o in Svc.Objects)
        {
            try
            {
                if (o is not IBattleNpc x || x.NameId != nameId || !x.IsCasting || x.CastActionId != castId) continue;
                if (maxElapsed == null || x.CurrentCastTime <= maxElapsed.Value) return true;
            }
            catch (NullReferenceException) { }
        }
        return false;
    }

    private bool IsDpsSide() => C.Role switch
    {
        RoleMode.Dps => true,
        RoleMode.Support => false,
        _ => IsDps(BasePlayer),
    };

    // ClassJob の Role: 1 タンク / 2 近接 / 3 遠隔 / 4 ヒーラー
    private static bool IsDps(IPlayerCharacter? pc) => pc != null && pc.ClassJob.Value.Role is 2 or 3;
    #endregion
}
