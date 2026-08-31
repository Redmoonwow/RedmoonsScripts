# 実装イディオム集

公式スクリプト 340 本 (`Splatoon/SplatoonScripts/`) から抽出した頻出パターン。
数字は実際の出現回数。**迷ったら公式の同種スクリプトを grep するのが最速。**

---

## 1. トリガの選び方

ギミックを何で検知するか。上から順に「安定していて実装が楽」。

| トリガ | override | 向いているもの | 注意 |
|---|---|---|---|
| 敵の詠唱 | `OnStartingCast(uint source, uint castId)` | 予兆のある AoE、フェーズ移行 | 一番安定。まずこれを探す |
| デバフ / バフ | `OnGainBuffEffect` / `OnRemoveBuffEffect` / `OnUpdateBuffEffect` | 頭割り・散開・線・DoT | `Status.Param` で種別が分かれることがある |
| map effect | `OnMapEffect(uint position, ushort data1, ushort data2)` | 床の光り方、ギミック盤面 | `position` は内部インデックス。`Controller.GetMapEffect` で解決 |
| VFX | `OnVFXSpawn(uint target, string vfxPath)` | 予兆エフェクト、マーカー | パスの部分一致で判定することが多い |
| 線 | `OnTetherCreate` / `OnTetherRemoval` | 鎖・引き寄せ・ペア | `data2/data3/data5` の意味は要調査 |
| object effect | `OnObjectEffect(uint target, uint entityId, uint actionId)` | ギミックオブジェクトの状態変化 | |
| action effect | `OnActionEffectEvent(ActionEffectSet set)` | 実際に飛んだダメージ・ノックバック | 発生後なので予告には使えない |
| actor control | `OnActorControl(...)` | 上記で拾えないもの | **VOLATILE**。毎パケット。最後の手段 |
| 毎フレーム | `OnUpdate()` | 位置追従、継続判定 | スロットラ必須 |

デバッグ用に `Splatoon/SplatoonScripts/Tests/` の `CastStartingTest.cs`, `ActorControlTest.cs`,
`DisplayMapEffect.cs`, `ObjectEffectTest.cs`, `StatusListMonitoring.cs`, `DMParser.cs` が使える。
未知のギミックを解析するときはこれらをロードしてログを取る。

---

## 2. Element の登録と操作

### 登録 (`OnSetup`)

Splatoon GUI で element を作り Export した JSON をそのまま raw string literal で貼る。
手で組むより速く、GUI の全機能が使える。

```csharp
public override void OnSetup()
{
    Controller.RegisterElementFromCode("Spread", """
        {"Name":"Spread","type":1,"radius":6.0,"color":3372220160,"Filled":false,
         "fillIntensity":0.5,"overlayTextColor":4278190335,"overlayVOffset":1.2,
         "overlayText":"<<< Spread >>>","refActorComparisonType":2}
        """);
}
```

複数を一気に:

```csharp
Controller.RegisterElementsFromMultilineCode("""
    {...}
    {...}
    """);
```

ループで連番登録するのも定番 (`$"Tower{i}"`)。

### 表示切り替え (最頻出パターン)

```csharp
// 132 回: 存在する前提
Controller.GetElementByName("Spread").Enabled = true;

// null 安全に書くならこちら (推奨)
if (Controller.TryGetElementByName("Spread", out var e))
{
    e.Enabled = true;
    e.SetRefPosition(targetPos);       // Vector3 をそのまま渡せる (Y/Z 入れ替えは自動)
    e.color = Controller.AttentionColor;
    e.overlayText = "STACK";
}
```

よく触るフィールド (出現数): `Enabled` 132 / `refActorObjectID` 29 / `SetRefPosition` 23 /
`color` 22 / `tether` 12 / `SetOffPosition` 10 / `thicc` 8

### 特定プレイヤーに追従させる

```csharp
if (Controller.TryGetElementByName("Marker", out var e))
{
    e.refActorObjectID = player.EntityId;   // refActorComparisonType = 2 (Object ID) の element
    e.Enabled = true;
}
```

### 消す

```csharp
public override void OnReset()
{
    Controller.Hide();      // element も layout も全部非表示
}
```

`OnReset` は wipe / commence / 戦闘開始 / 戦闘終了 / 無効化の直前に呼ばれる。
**フェーズをまたぐ指示を消し忘れないために、ここは必ず書く。**

---

## 3. パーティメンバーの取得

```csharp
// 非 cross-world 限定。duty recorder 対応。Splatoon スクリプトの標準
foreach (var pc in Controller.GetPartyMembers()) { ... }

Controller.GetPartyMembers().Where(x => x.EntityId != BasePlayer.EntityId)   // 14 回
Controller.GetPartyMembers().Any(x => ...)                                   // 11 回
Controller.GetPartyMembers().FirstOrDefault(x => ...)                        //  5 回

// cross-world / アライアンスを含める
foreach (var m in UniversalParty.Members) { var obj = m.IGameObject; if (obj == null) continue; ... }

// ソロでも「自分だけの疑似パーティ」が欲しいとき (Framework スレッド限定)
foreach (var pc in FakeParty.Get()) { ... }
```

### ロールで分ける

```csharp
using ECommons.GameFunctions;

var tanks   = Controller.GetPartyMembers().Where(x => x.GetRole() == CombatRole.Tank);
var healers = Controller.GetPartyMembers().Where(x => x.GetRole() == CombatRole.Healer);
var myRole  = Controller.RolePosition;      // T1/T2/H1/H2/M1/M2/R1/R2
```

---

## 4. 自機

```csharp
BasePlayer                  // IPlayerCharacter。これを使う
BasePlayer.EntityId
BasePlayer.Position
BasePlayer.Name.ToString()
BasePlayer.GetJob()         // using ECommons.GameHelpers.LegacyPlayer;
```

`Player.Object` / `Svc.Objects.LocalPlayer` は Base Player Override を壊すので使わない
(公式では RS0030 でエラー)。

---

## 5. オブジェクト検索

```csharp
using Splatoon.SplatoonScripting;   // uint 拡張

// Entity ID から
if (source.TryGetObject(out var obj)) { ... }
if (source.TryGetBattleNpc(out var npc)) { ... }
if (source.TryGetPlayer(out var pc)) { ... }
var o = someId.GetObject();          // 336 回

// テーブル走査 (419 回)
var boss = Svc.Objects.FirstOrDefault(x => x.DataId == 12345);
foreach (var x in Svc.Objects.OfType<IBattleNpc>()) { ... }
Svc.Objects.Where(x => x.NameId == 4567 && x.IsTargetable())
```

判定に使う識別子:

| プロパティ | 出現数 | 説明 |
|---|---|---|
| `DataId` | 226 | モデル/NPC の種別 ID。ボス・雑魚の判定はこれ |
| `NameId` | 94 | ネームプレート名 ID |
| `EntityId` | — | インスタンス固有 ID |
| `StatusList` | 324 | バフ/デバフ一覧 (`IBattleChara`) |
| `IsCasting` | 94 | 詠唱中か |

---

## 6. デバフ・バフ判定

```csharp
using ECommons.GameFunctions;

// 単発
if (npc.HasStatus(1234u)) { ... }
if (npc.HasStatus(1234u, out var remaining)) { ... }
if (npc.HasStatus(1234u, lessThan: 5f)) { ... }        // 残り 5 秒未満

// 複数のうちどれか
if (npc.HasStatus([1234u, 1235u], out var found)) { ... }

// 生の StatusList を回す
var s = pc.StatusList.FirstOrDefault(x => x.StatusId == 1234);
if (s != null) { var param = s.Param; var left = s.RemainingTime; }
```

`Status.Param` (スタック数 / 種別) でギミックの分岐が決まることが多い。

イベント側:

```csharp
public override void OnGainBuffEffect(uint sourceId, Status Status)
{
    if (Status.StatusId != 5084) return;
    if (!sourceId.TryGetPlayer(out var pc)) return;
    ...
}
```

---

## 7. 頭上マーカー

```csharp
using Splatoon.Memory;

if (Marking.HaveMark(character, index)) { ... }    // 33 回。index は 0 始まり
```

`MarkingController.Instance()->Markers` を直接読むより、こちらを使う。

---

## 8. VFX と線 (経過時間つき)

```csharp
using Splatoon.Memory;

// この VFX が出てから 1 秒以内か
if (obj.TryGetSpecificVfxInfo("vfx/lockon/eff/some_path.avfx", out var info) && info.AgeF < 1f)
{
    ...
}

// 対象についている全 VFX
if (obj.TryGetVfx(out var fx))
{
    foreach (var (path, i) in fx) { if (i.AgeF < 0.5f) ... }
}

// 線
foreach (var t in AttachedInfo.GetOrCreateTetherInfo(obj.Address))
{
    if (t.AgeF < 1f && t.ParamEqual(p1, p2, p3)) ...
}

// ECommons 側の線取得
var tethers = character.GetTethers();       // List<TetherInfo>: Id, IsSource, PairId, Pair
```

---

## 9. 角度・座標計算

```csharp
using ECommons.MathHelpers;

var center = new Vector2(100, 100);

// 中心から見た対象の方位 (【度】)
var angle = MathHelper.GetRelativeAngle(center, obj.Position.ToVector2());

// 点を中心まわりに回す (【ラジアン】) — 単位変換を忘れない
var p = MathHelper.RotateWorldPoint(new Vector3(100, 0, 100), 45f.DegToRad(), new Vector3(100, 0, 92));

// 8 分割の塔座標を作る (P2_Forsaken の実例)
for (uint i = 1; i <= 8; i++)
    towers[i] = MathHelper.RotateWorldPoint(new(100, 0, 100), (45f * (i - 1)).DegToRad(), new(100, 0, 92)).ToVector2();

// 時計回りに並べる
var ordered = MathHelper.EnumerateObjectsClockwise(objects, x => x.Position.ToVector2(), center, startAngleDegrees);

// 方角
var dir = MathHelper.GetCardinalDirection(center, obj.Position.ToVector2());

// 距離 (最頻出)
Vector3.Distance(a.Position, b.Position)        // 173 回
Vector2.Distance(a2, b2)                         // 110 回
```

`Vector3.ToVector2()` は **Y を捨てて (X, Z)** にする。高さを無視した平面距離を測るときの定番。

---

## 10. 設定 (`SplatoonScript<TConfig>`)

```csharp
public unsafe class My_Script : SplatoonScript<My_Script.Config>
{
    public override void OnSettingsDraw()
    {
        ImGui.Checkbox("Show all players", ref C.ShowAll);
        if (!C.ShowAll)
        {
            ImGui.Indent();
            ImGui.Checkbox("Show your partner", ref C.ShowOnlyPartner);
            if (C.ShowOnlyPartner) C.Partner.Draw();      // Priority の UI がそのまま出る
            ImGui.Unindent();
        }
        ImGuiEx.EnumCombo("Strategy", ref C.Strategy);
        ImGuiEx.HelpMarker("説明");
    }

    public class Config
    {
        public bool ShowAll = true;
        public bool ShowOnlyPartner = false;
        public HashSet<uint> Switchers = [1, 2, 5, 6];
        public Prio1 Partner = new();
    }

    public class Prio1 : PriorityData
    {
        public override int GetNumPlayers() => 1;
    }
}
```

- 設定クラスは素の POCO でよい (`IEzConfig` は `[Obsolete]`)。
- 保存は自動。明示保存が要るときだけ `Controller.SaveConfig()`。
- `C` は `Controller.GetConfig<Config>()` のエイリアス。

---

## 11. Priority (誰が何番目か)

```csharp
public class Prio : PriorityData { public override int GetNumPlayers() => 8; }

// OnSettingsDraw
C.Prio.Draw();

// 解決
var first = C.Prio.GetPlayer(x => x.IGameObject != null, 1);           // position は 1 始まり
var myIdx = C.Prio.GetOwnIndex(x => x.IGameObject != null);            // 0 始まり、なければ -1
var all   = C.Prio.GetPlayers(x => x.IGameObject != null);
```

`UniversalPartyMember.IGameObject` は **null になり得る** (別ワールド・範囲外)。必ず弾く。

---

## 12. 遅延・間引き

```csharp
// スクリプト専用スロットラ (静的版と違い他スクリプトと衝突しない)
if (EzThrottler.Throttle("check", 500)) { ... }        // 500ms に 1 回
if (FrameThrottler.Throttle("draw", 10)) { ... }        // 10 フレームに 1 回

// 遅延実行 (Reset で自動キャンセルされる)
Controller.Schedule(() => { element.Enabled = false; }, 5000);

// 一定時間後に丸ごとリセット
Controller.ScheduleReset(10000);

// 逐次処理
Controller.TaskManager.Enqueue(() => SomeCondition(), "wait for condition");
Controller.TaskManager.EnqueueDelay(1000);
Controller.TaskManager.Enqueue(() => { DoThing(); });
```

`OnUpdate` に重い処理を直書きしない。`OnActorControl` はさらに高頻度なので特に注意。

---

## 13. 画面中央の指示表示

```csharp
public override void OnUpdate()
{
    if (!shouldShow) return;
    Controller.DisplayAttentionWindowLine(EColor.RedBright, "$1 と頭割り", partnerName);
}
```

- **毎フレーム呼び続けないと消える。**
- 渡した `Action` は同フレームでは実行されず、複数回呼ばれ得る。副作用を入れない。

---

## 14. ログ・デバッグ

```csharp
using ECommons.Logging;

PluginLog.Information($"cast {castId} from {source}");   // ログのみ
DuoLog.Information("見えるログ");                          // ログ + ゲーム内チャット
```

Splatoon の Scripting タブにログ表示がある。解析中は `DuoLog`、完成後は `PluginLog` に落とす。

---

## 15. 多言語

```csharp
var text = Loc(en: "Stack", jp: "頭割り", de: null, fr: null, cn: null);
```

element 側は `overlayTextIntl` / `refActorNameIntl` / `InternationalName` (`InternationalString`)
に言語別の値を持てる。

---

## 16. 参考になる公式スクリプト

| 目的 | ファイル |
|---|---|
| 設定 + Priority + element 大量登録の総合例 | `Duties/Dawntrail/Dancing Mad/P2_Forsaken.cs` |
| 詠唱トリガの基本形 | `Duties/Dawntrail/M11S Flame Breath.cs` |
| ギミック解析用のダンプ | `Tests/CastStartingTest.cs`, `Tests/ActorControlTest.cs`, `Tests/DisplayMapEffect.cs` |
| ステータス監視 | `Tests/StatusListMonitoring.cs` |
| 他プラグイン IPC | `Tests/IPCExample.cs` |
| Priority の使い方 | `Tests/PriorityTest.cs` |
| 汎用ユーティリティ (duty 外で動くもの) | `Generic/` 以下 |
