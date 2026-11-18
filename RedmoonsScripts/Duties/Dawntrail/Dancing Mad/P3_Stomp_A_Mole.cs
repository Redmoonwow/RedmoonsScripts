using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using Splatoon;
using Splatoon.SplatoonScripting;

namespace RedmoonsScripts.Duties.Dawntrail.Dancing_Mad;

/// <summary>
/// Dancing Mad (Ultimate) P3 どんどこ地団駄 (Stomp a mole) の立ち位置。
/// </summary>
/// <remarks>
/// Splatoon のレイアウト「[Select configuration] Dmad P3 Stomp a mole - dps (rel south)」と
/// 「… - support (rel north)」(グループ「絶妖星乱舞 - NXIV &amp; mirage(translate)」) をスクリプトに写したもの。
/// レイアウトの条件付き表示 (Conditional) を同じ順にコードで書いた。描く物・位置・色はレイアウトのまま。
///
/// 条件 (どれもレイアウトと同じ):
///   ブリザガ    NameID 6052 が 47887 を詠唱中
///   地団駄 4 秒  NameID 7131 が 47855 を詠唱して 4 秒以内
///   頭割り      com_share3t の VFX が 5 秒以内に付いた (誰か / 自分のロールの誰か)
/// 表示:
///   ブリザガ中                         → [A] ブリザガの位置 + [B] 予備の位置
///   自分のロールに頭割り & 地団駄 4 秒以内 → 両方の塔に「入らない」 + [C] 予備の位置
///   相手のロールに頭割り & 地団駄 4 秒以内 → [D] 自分の塔 + [E] 予備の位置
///   相手のロールに頭割り & それ以降       → [F] 自分の塔 (線あり)
///   自分のロールに頭割り & それ以降       → 両方の塔に「入らない」 + [G] 中央
/// 位置はケフカ (NameID 7131) の向きに対する相対。Left = ケフカの顔を見て左 (+X)。
/// DPS は南寄り (+Y 9)、タンク・ヒーラーは北寄り (-Y 9)。ロールはジョブから自動 (設定で固定もできる)。
///
/// レイアウトからの変更は 1 つだけ: 塔 ([F]) から中央 ([G]) へ切り替えるのを、自分に塔の地団駄 (47856) が当たるまで待つ。
/// 47856 は塔の 4 人に 1 人ずつ約 1.3 秒おきに当たり、次の頭割りのマーカーは 1 人目が当たった直後に付く。
/// レイアウトはマーカーで切り替えていたので、後半に踏まれる人 (D2・D4) は塔にいる間に中央が出ていた。
///
/// v4: 塔・中央から次に移るのは、その回の技の結果 (OnActionEffectApplied) が出てから。塔は自分に地団駄 (47856)、中央は頭割り (47875)。
///     次のマーカーは結果より先に付くので、マーカーで切り替えると早かった (v1 の「自分に当たるまで塔」もこれに置き換え)。
/// v3: 「自分のロールに頭割り」は同じロールの誰か 1 人に付いたら (v1・v2 は 4 人全員で、マーカーは 1 人にしか付かないので中央に移らなかった)。
/// v2: 条件のつなぎ目 (ブリザガの着弾 → 1 回目のマーカー、5 秒の窓 → 次のマーカー) で表示が一瞬消えるので、空になったら直前の表示を 1.5 秒残す。
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
    }
    #endregion

    #region const
    /********************************************************************/
    /* const                                                            */
    /********************************************************************/
    private const uint BlizzardNameId = 6052;
    private const uint BlizzardCast = 47887;
    private const uint KefkaNameId = 7131;
    private const uint StompCast = 47855;
    private const float StompEarlySeconds = 4f;
    private const string StackVfx = "vfx/lockon/eff/com_share3t.avfx";
    private const long StackVfxMs = 5000;
    private const uint StompHit = 47856;       // 塔の 4 人に 2 人ずつ約 1.3 秒おきに当たる
    private const uint StackHit = 47875;       // 頭割りの着弾
    private const long ResolveHoldMaxMs = 6000; // 結果が取れなかった時の保険

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
    public override Metadata? Metadata => new(4, "Redmoon");
    #endregion

    #region private properties
    /********************************************************************/
    /* private properties                                               */
    /********************************************************************/
    private Config C => Controller.GetConfig<Config>();
    private readonly Dictionary<uint, long> _stackVfxAt = [];   // EntityId → 頭割りの VFX が付いた時刻 (TickCount64)
    // 塔・中央は、その回の技の結果 (Applied) が出るまで次を出さない。次の頭割りのマーカーは結果より先に付く
    // (例: マーカー 56.595 → 着弾 56.655 → 塔の 1 組目 56.725 → 2 組目 58.040)
    private enum Spot { None, Tower, Middle }
    private Spot _holdSpot = Spot.None;
    private readonly List<string> _holdNames = [];
    private long _holdStartMs;
    private long _towerHitMs;   // 地団駄 (47856) の結果が自分に出た時刻
    private long _stackHitMs;   // 頭割り (47875) の結果が出た時刻

    // 条件のつなぎ目で表示が空になった時、直前の表示をこの間だけ残す。
    // 隙間: ブリザガの着弾 → 1 回目の頭割りのマーカー (約 0.4 秒)、マーカーの 5 秒の窓 → 次のマーカー (約 0.5 秒。間隔は約 5.5 秒)
    private const long GapHoldMs = 1500;
    private readonly List<string> _shown = [];
    private readonly List<string> _lastShown = [];
    private long _lastShownMs;
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

    public override void OnVFXSpawn(uint target, string vfxPath)
    {
        if (vfxPath == StackVfx) _stackVfxAt[target] = Environment.TickCount64;
    }

    public override void OnReset()
    {
        _stackVfxAt.Clear();
        _holdSpot = Spot.None;
        _holdNames.Clear();
        _towerHitMs = _stackHitMs = 0;
        _lastShown.Clear();
    }

    // 技の結果が実際に出た瞬間 (OnActionEffectApplied)。塔は自分に当たった時、頭割りは誰かに当たった時
    public override void OnActionEffectApplied(ECommons.Hooks.ActionEffectTypes.ActionEffectSet set, int targetIndex, ActionEffectResult result)
    {
        var id = set.Header.ActionID;
        var now = Environment.TickCount64;
        if (id == StompHit && BasePlayer != null && (result.TargetId == BasePlayer.EntityId || result.RecipientId == BasePlayer.EntityId)) _towerHitMs = now;
        else if (id == StackHit) _stackHitMs = now;
    }

    public override void OnUpdate()
    {
        HideAll();
        _shown.Clear();
        if (BasePlayer == null) return;

        var now = Environment.TickCount64;
        Decide(now);
        HoldUntilResolved(now);
        if (_shown.Count > 0)
        {
            _lastShown.Clear();
            _lastShown.AddRange(_shown);
            _lastShownMs = now;
        }
        else if (_lastShown.Count > 0 && now - _lastShownMs <= GapHoldMs)
        {
            _shown.AddRange(_lastShown);
        }
        foreach (var name in _shown)
            if (Controller.TryGetElementByName(name, out var e)) e.Enabled = true;
    }

    private void Decide(long now)
    {
        var dps = IsDpsSide();
        var r = dps ? "D" : "S";
        var lr = C.Side == Side.Left ? "L" : "R";

        var blizzard = IsCasting(BlizzardNameId, BlizzardCast, null);
        var stompEarly = IsCasting(KefkaNameId, StompCast, StompEarlySeconds);
        var anyStack = _stackVfxAt.Values.Any(t => now - t <= StackVfxMs);
        var myRoleStack = RoleMembers(dps) is { Count: > 0 } members
                          && members.Any(m => _stackVfxAt.TryGetValue(m.EntityId, out var t) && now - t <= StackVfxMs);

        if (blizzard)
        {
            Show(C.Strat == Strat.Regular ? "A_Middle" : $"A_Rel_{r}");
            Show($"B_{lr}_{r}");
        }
        if (myRoleStack && stompEarly)
        {
            Show("NoTower_L"); Show("NoTower_R"); Show($"Pre_{lr}_{r}");
        }
        if (anyStack && !myRoleStack && stompEarly)
        {
            Show($"D_{lr}"); Show($"Pre_{lr}_{r}");
        }
        if (anyStack && !myRoleStack && !stompEarly)
        {
            Show($"F_{lr}");
        }
        if (myRoleStack && !stompEarly)
        {
            Show("NoTower_L"); Show("NoTower_R"); Show("G_Middle");
        }
    }

    // 塔・中央を出している間に表示が変わろうとしたら、その回の結果が出るまで今の表示のまま。
    // 塔 = 自分に地団駄 (47856) の結果が出るまで (2 組目に踏まれる D2・D4 は 1 組目より約 1.3 秒遅い)、中央 = 頭割り (47875) の結果が出るまで
    private void HoldUntilResolved(long now)
    {
        var spot = _shown.Any(n => n.StartsWith("F_")) ? Spot.Tower : _shown.Contains("G_Middle") ? Spot.Middle : Spot.None;
        if (_holdSpot != Spot.None && spot != _holdSpot)
        {
            var resolvedAt = _holdSpot == Spot.Tower ? _towerHitMs : _stackHitMs;
            if (resolvedAt < _holdStartMs && now - _holdStartMs <= ResolveHoldMaxMs)
            {
                _shown.Clear();
                _shown.AddRange(_holdNames);
                return;
            }
            _holdSpot = Spot.None;
        }
        if (spot != Spot.None)
        {
            if (spot != _holdSpot) _holdStartMs = now;
            _holdSpot = spot;
            _holdNames.Clear();
            _holdNames.AddRange(_shown);
        }
    }

    public override void OnSettingsDraw()
    {
        ImGuiEx.EnumCombo("作戦##strat", ref C.Strat);
        ImGuiEx.EnumCombo("ケフカの顔を見て左右##side", ref C.Side);
        ImGuiEx.EnumCombo("ロール (Auto = ジョブから)##role", ref C.Role);
        ImGuiEx.Text($"今の判定: {(IsDpsSide() ? "DPS (南寄り)" : "タンク・ヒーラー (北寄り)")} / 頭割りの記録 {_stackVfxAt.Count} 件");
    }
    #endregion

    #region private methods
    /********************************************************************/
    /* private methods                                                  */
    /********************************************************************/
    private static Element Fixed(uint color, bool tether) => new(0)
    {
        refX = 100f, refY = 100f, radius = 2f, Donut = 0.5f, color = color, Filled = true, fillIntensity = 0.5f, thicc = 5f, tether = tether,
    };

    // ケフカ (NameID 7131) の向きに対する相対位置。レイアウトと同じく onlyVisible
    private static Element Rel(float x, float y, float radius, float donut, uint color, float thicc, float fill, bool tether) => new(1)
    {
        refActorNPCNameID = KefkaNameId, refActorComparisonType = 6, includeRotation = true, onlyVisible = true,
        offX = x, offY = y, radius = radius, Donut = donut, color = color, Filled = true, fillIntensity = fill, thicc = thicc, tether = tether,
    };

    private void Show(string name) => _shown.Add(name);

    private void HideAll()
    {
        foreach (var e in Controller.GetRegisteredElements()) e.Value.Enabled = false;
    }

    // NameID の敵がその技を詠唱中か。maxElapsed があれば詠唱開始からその秒数以内だけ
    private static bool IsCasting(uint nameId, uint castId, float? maxElapsed) =>
        Svc.Objects.OfType<IBattleNpc>().Any(x => x.NameId == nameId && x.IsCasting && x.CastActionId == castId
                                                 && (maxElapsed == null || x.CurrentCastTime <= maxElapsed.Value));

    private bool IsDpsSide() => C.Role switch
    {
        RoleMode.Dps => true,
        RoleMode.Support => false,
        _ => IsDps(BasePlayer),
    };

    // ClassJob の Role: 1 タンク / 2 近接 / 3 遠隔 / 4 ヒーラー
    private static bool IsDps(IPlayerCharacter? pc) => pc != null && pc.ClassJob.Value.Role is 2 or 3;

    // レイアウトの <d1>〜<d4> / <h1> <h2> <t1> <t2> (refActorComparisonAnd = 「その人であること」と「VFX」の両方。4 人全員ではない)
    private List<IPlayerCharacter> RoleMembers(bool dps) => Controller.GetPartyMembers().Where(p => IsDps(p) == dps).ToList();
    #endregion
}
