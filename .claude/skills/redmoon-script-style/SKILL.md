---
name: redmoon-script-style
description: Redmoon 本人の Splatoon スクリプトの書き方 (作法・テンプレート・自作 API)。固定 8 人パーティ前提のジョブ→スロット割り当てと、8 人全員分を解決してから自分の分だけ描く設計、State enum による状態機械、OnSetup で最小 Element を登録し ApplyElement で実行時に配置する方式、DirectionCalculator / ClockDirectionCalculator を核にした 8 方向・時計方向の抽象、#region の固定テンプレート、Debug 専用の OnSettingsDraw、リプレイ安全な自動化 (vnav / pdrspeed / Arm's Length)。本人のスクリプトを新規に書く・既存を直す・レビューするときに使う。
---

# Redmoon 流スクリプト作法

出典: `Splatoon/SplatoonScripts/Duties/Dawntrail/The Futures Rewritten/FullToolerPartyOnlyScrtipts/`
の 12 本 (`P1 Burn Strike Tower` を除く / 計 13,526 行) を解析したもの。数値は実測値。
基準コミットは Splatoon `d5017695` (2026-08-27)。

> **書式については注意。** これらのファイルは 2025-04-15 の公式リポジトリ横断コミット
> `398eb817 "Auto-refactor"` で機械的に整形されている (12 ファイル / 約 1,760 行が書き換え)。
> したがってリポジトリ上の書式 = 本人の書式ではない。素の書き方を見たいときは、この refactor から
> 漏れている `P2 Diamond Dust Full Toolers.cs` を参照する。
> 設計 (§4〜§9) は本人の手によるもので、refactor の影響を受けていない。

この作法は **固定 8 人パーティ (Full Tooler Party) 専用**という前提から全部が導かれている。
「誰が何をするか」をユーザに設定させず、**ジョブから機械的に決める**。だから Priority も
設定項目もほぼ無く、その代わり自作 API が厚い。

---

## 1. 一目でわかる特徴

| 項目 | 本人の流儀 | 公式スクリプトの一般解 |
|---|---|---|
| 基底クラス | `internal class X : SplatoonScript` (12/12) | `public class` / `SplatoonScript<T>` |
| Element 登録 | `RegisterElement(name, new Element(n){...})` **64 回** | `RegisterElementFromCode` (JSON) |
| 位置指定 | `ApplyElement(...)` **314 回** (自作ラッパ) | element を直接いじる |
| 方向の抽象 | `DirectionCalculator` **551 回** | `MathHelper` を直接 |
| 自機 | `Player.Object` **30 回** / `BasePlayer` **0 回** | `BasePlayer` |
| 誰が誰か | `SetListEntityIdByJob()` でジョブ→固定 index | `PriorityData` (使用 0 回) |
| 解決の範囲 | **8 人全員分を解決**してから自分の分だけ描く | 自分の分だけ解決する |
| 進行管理 | `State` enum + `_state = State.X` **71 回** | 素朴なフラグ |
| クリーンアップ | `HideAllElements()` **72 回** | `Controller.Hide()` |
| 設定画面 | ほぼ Debug パネルのみ (11/12) | ユーザ向けオプション |
| 例外処理 | `catch` **0 回**。ガード節 + `ExceptionReturn()` | try/catch |
| ログ | `DuoLog` 56 / `PluginLog` 20 | `PluginLog` 中心 |
| 自動化 | `Chat.Instance.ExecuteCommand` **64 回** (`/vnav`, `/pdrspeed`, `/mk off`) | 表示のみ |

主トリガは **`OnStartingCast` と `OnActionEffectEvent` の 2 本**(12/12 が両方を実装)。
補助的に `OnTetherCreate` 7、`OnVFXSpawn` 3、`OnActorControl` 3、`OnMapEffect` 2、
`OnGainBuffEffect` 2。`OnDirectorUpdate` と `OnObjectEffect` は 0。

---

## 2. ファイル骨格 (P3 以降の完成形テンプレート)

`#region` の並び順は 8 本すべてで完全に一致している。各 region の直後に
72 桁のバナーコメントを置く (P3 以降の 8 ファイルすべてで 16 個 = region 8 個 × 開始バナー)。

```csharp
using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons;
using ECommons.Automation;
using ECommons.Configuration;
using ECommons.ExcelServices;
using Player = ECommons.GameHelpers.LegacyPlayer.Player;
using ECommons.GameHelpers.LegacyPlayer;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using ECommons.Hooks.ActionEffectTypes;
using ECommons.ImGuiMethods;
using ECommons.Logging;
using ECommons.MathHelpers;
using Dalamud.Bindings.ImGui;
using Splatoon;
using Splatoon.SplatoonScripting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ECommons.DalamudServices.Legacy;

namespace RedmoonsScripts.Duties.Dawntrail.The_Futures_Rewritten;
internal class P4_Something_Full_Toolers : SplatoonScript
{
    #region types
    /********************************************************************/
    /* types                                                            */
    /********************************************************************/
    private enum State
    {
        None = 0,
        FirstMechanic,
        SecondMechanic,
    }
    #endregion

    #region class
    /********************************************************************/
    /* class                                                            */
    /********************************************************************/
    public class Config : IEzConfig
    {
        public bool Master = false;
    }

    private class PartyData
    {
        public int Index { get; set; }
        public bool Mine = false;
        public uint EntityId;
        public IPlayerCharacter? Object => (IPlayerCharacter)EntityId.GetObject()! ?? null;

        // ギミック固有の状態はここに足していく
        public DirectionCalculator.Direction TowerDirection = DirectionCalculator.Direction.None;

        public bool IsTank => TankJobs.Contains(Object?.GetJob() ?? Job.WHM);
        public bool IsHealer => HealerJobs.Contains(Object?.GetJob() ?? Job.PLD);
        public bool IsTH => IsTank || IsHealer;
        public bool IsMeleeDps => MeleeDpsJobs.Contains(Object?.GetJob() ?? Job.MCH);
        public bool IsRangedDps => RangedDpsJobs.Contains(Object?.GetJob() ?? Job.MNK);
        public bool IsMagicDps => MagicDpsJobs.Contains(Object?.GetJob() ?? Job.WHM);
        public bool IsDps => IsMeleeDps || IsRangedDps || IsMagicDps;

        public PartyData(uint entityId, int index)
        {
            EntityId = entityId;
            Index = index;
            Mine = entityId == Player.Object.EntityId;
        }
    }
    #endregion

    #region const
    /********************************************************************/
    /* const                                                            */
    /********************************************************************/
    private readonly List<(DirectionCalculator.Direction, Vector3)> vector3List =
    [
        (DirectionCalculator.Direction.North, new Vector3(96f, 0f, 95f)),
        // 実測した座標をそのまま並べる
    ];
    #endregion

    #region public properties
    /********************************************************************/
    /* public properties                                                */
    /********************************************************************/
    public override HashSet<uint>? ValidTerritories => [1238];
    public override Metadata? Metadata => new(1, "redmoon");
    #endregion

    #region private properties
    /********************************************************************/
    /* private properties                                               */
    /********************************************************************/
    private Config C => Controller.GetConfig<Config>();
    private State _state = State.None;
    private List<PartyData> _partyDataList = [];
    #endregion

    #region public methods
    /********************************************************************/
    /* public methods                                                   */
    /********************************************************************/
    public override void OnSetup() { /* Element を最小定義で登録 */ }
    public override void OnStartingCast(uint source, uint castId) { }
    public override void OnActionEffectEvent(ActionEffectSet set) { }
    public override void OnUpdate() { }
    public override void OnReset() { }
    public override void OnSettingsDraw() { }
    #endregion

    #region private methods
    /********************************************************************/
    /* private methods                                                  */
    /********************************************************************/
    // SetState / Show*** / Parse*** / GetMinedata など
    #endregion

    #region API
    /********************************************************************/
    /* API                                                              */
    /********************************************************************/
    // 下記「自作 API ライブラリ」をまるごと貼る。中身は編集しない
    #endregion
}
```

> P1/P2 の古い 4 本は region 名が `enums` / `Enums` / `public Fields` だったり
> `#region const` が無かったりする。**新規は P3 以降の形に揃える。**

---

## 3. 命名

| 対象 | 規則 | 例 |
|---|---|---|
| クラス | ファイル名の空白を `_` に | `P4_Darklit__Full_Toolers` |
| private フィールド | `_camelCase` | `_state`, `_partyDataList`, `_akhRhaiCount`, `_wing` |
| 一部の状態フラグ | `_PascalCase` (揺れている) | `_StateProcEnd`, `_StateProcEndCommon` |
| static 定数テーブル | PascalCase / camelCase 混在 | `TankJobs`, `jobOrder`, `vector3List` |
| 設定アクセサ | `private Config C => Controller.GetConfig<Config>();` | |
| 表示メソッド | `Show***` | `ShowAkhRhai`, `ShowTowerStateGuide`, `ShowSplit` |
| 解析メソッド | `Parse***` | `ParseTether` |
| 状態遷移 | `SetState(...)` | 状態 enum ごとにオーバーロード |
| 全消し | `HideAllElements()` | |
| 自分のデータ | `GetMinedata()` | 55 回。**`Mydata` ではなく `Minedata`** |
| Element 名 | `"Bait"`, `"BaitObject"`, `$"Circle{i}"`, `$"ConeRange{i}"` | 連番はループ登録 |

`Metadata` の作者名は **小文字 `"redmoon"`** (10/12)。P1 の 2 本だけ `"Redmoon"`。
新規は `"redmoon"` に揃える。

---

## 4. 固定パーティ前提の設計

これが全体の土台。**ユーザに誰が誰かを設定させない。**

```csharp
private void SetListEntityIdByJob()
{
    _partyDataList.Clear();
    for(var i = 0; i < 8; i++) _partyDataList.Add(new PartyData(0, i));

    foreach(var pc in FakeParty.Get())
    {
        switch(pc.GetJob())
        {
            case Job.WAR: case Job.DRK: case Job.GNB: _partyDataList[0].EntityId = pc.EntityId; break;
            case Job.PLD:                              _partyDataList[1].EntityId = pc.EntityId; break;
            // ... 以下、ジョブ → 固定 index
        }
    }
}
private PartyData? GetMinedata() => _partyDataList.Find(x => x.Mine);
```

- **index が役割**。「index 0 = MT」のように固定で、以後 `pc.Index == 0` で分岐する。
- パーティ取得は `FakeParty.Get()` (16 回)。`Controller.GetPartyMembers()` は使わない。
- 自分の判定は `PartyData.Mine`。コンストラクタで `entityId == Player.Object.EntityId` として決める。
- 各種ロール判定 (`IsTank` / `IsTH` / `IsMeleeDps` …) は `PartyData` の計算プロパティにする。
- ギミック中に確定する情報 (`TowerDirection`, `ConeIndex`, `IsStack`, `SplitPos`, `TetherPairId1/2`)
  は全部 `PartyData` に生やす。**状態を 1 箇所 (`_partyDataList`) に集約する。**

呼び出しは「ギミック開始の詠唱を拾った瞬間」に `SetListEntityIdByJob()` を実行して作り直す。

### 8 人全員分を解決してから、自分の分だけ描く

**これが他の書き手との一番大きな違い。** 表示に必要なのは自分の担当だけなのに、
ギミックの割り当ては必ず 8 人全員について解く。理由は 3 つ。

1. **整合性を検証できる。** 全員解けているかを数で確かめられるので、読み違えたまま
   間違った指示を出さずに済む。
2. **デバッグできる。** Debug テーブルに 8 人分が並ぶので、自分の表示が出ない/おかしいときに
   「誰の解決で崩れたか」がその場で分かる。録画を再生しながら他人の行に着目できる。
3. **表示先を差し替えられる。** 「自分」を別の誰かに切り替えれば、他人視点の検証がそのまま通る
   (実際、`_partyDataList.Each(x => x.Mine = false); _partyDataList[6].Mine = true;` を
   コメントアウトで残してあるファイルがある)。

書き方は「解決」と「描画」を分けるだけ。

```csharp
// 解決: 8 人全員の TowerDirection / IsStack / TetherPairId2 … を埋める。bool で成否を返す
private bool ParseTether()
{
    foreach(var pc in _partyDataList)          // 全員をなめる
    {
        if(pc.TetherPairId1 == 0) continue;
        var pair2 = _partyDataList.Find(x => x.TetherPairId1 == pc.EntityId);
        if(pair2 == null) continue;
        pc.TetherPairId2 = pair2.EntityId;
    }

    foreach(var pc in FakeParty.Get().Where(x => x.StatusList.Any(y => y.StatusId == 2461)))
        _partyDataList.Find(x => x.EntityId == pc.EntityId)!.IsStack = true;

    // 全員分あるからこそ書ける整合性チェック
    if(_partyDataList.Where(x => x.IsStack).Count() != 2) return false;
    if(_partyDataList.Where(x => x.TetherPairId1 != 0 && x.TetherPairId2 != 0).Count() != 4) return false;

    // 線付きヒラは北確定 → つながっている 2 人は南 → 残りを埋める …
    return true;
}

// 呼ぶ側: 解決できたときだけ状態を進める
if(ParseTether()) { ShowTowerStateGuide(); _state = State.tower; }
else              { _state = State.None; }

// 描画: ここで初めて自分に絞る
private void ShowTowerStateGuide()
{
    var pc = GetMinedata();
    if(pc == null) return;
    ApplyElement("Bait", pc.TowerDirection, 10);
}
```

- 解決メソッドは `Parse***` で **`bool` を返す** (`ParseTether`, `ParseDebuff`)。
  途中で辻褄が合わなければ `return false;` (12 本で計 41 箇所)。
- 数による検証がそのまま安全弁になる。実際に書かれているもの:

  ```csharp
  _partyDataList.Where(x => x.TowerDirection != Direction.None).Count() != 8   // 全員解けたか
  _partyDataList.Where(x => x.Mine).ToList().Count != 1                        // 自分は 1 人か
  _partyDataList.Where(x => x.IsStack).Count() != 2                            // 頭割りは 2 人か
  _partyDataList.Where(x => x.TetherPairId1 != 0 && x.TetherPairId2 != 0).Count() != 6
  ```
  外れたら `ExceptionReturn("...")` でログに残して抜ける。**黙って描かない。**
- 描画側 (`Show***`) の 1 行目はほぼ必ず `var pc = GetMinedata(); if(pc == null) return;`。
  **絞り込みは描画の入口 1 箇所だけ**にして、解決ロジックには `Mine` を持ち込まない。
- 解決結果は必ず `PartyData` のフィールドに書く。ローカル変数で済ませない
  (Debug テーブルに出せなくなるため)。

---

## 5. 状態機械

```csharp
private enum State { None = 0, FirstMechanic, SecondMechanic }   // 必ず None = 0 始まり
private State _state = State.None;
```

- 全ハンドラの先頭で `if(_state == State.None) return;` (未開始なら何もしない)
- 遷移のたびに `HideAllElements()` → 新しい表示 → `_state = State.X` の順
- 複雑な場面では `SetState(...)` に儀式をまとめる:

```csharp
private void SetState(StateAeloFirst state)
{
    HideAllElements();
    ResetCircleElement();
    _StateProcEnd = false;
    _StateProcEndCommon = false;
    _lastVnavPos = Vector3.Zero;
    _aeloFirst = state;
}
```

- 自分の担当ルートが分岐する大型ギミックは **ルートごとに別の State enum** を切る
  (`P4 Crystallize Time` は `StateCommon` / `StateAeloFirst` / `StateAeloSecond` /
  `StateRedIce3` / `StateBlueFirst` / `StateBlueSecond` の 6 本 + `WaveState` + `Gimmick`)
- ルートが確定したら **delegate に差す**:

```csharp
private delegate void MineRoleAction();
private MineRoleAction? _mineRoleAction = null;
// 判定時
_mineRoleAction = RedAeloFirst;   // 1人
// OnUpdate で
if(_mineRoleAction != null && _slowHourGlassDirection != Direction.None) _mineRoleAction();
```

- `OnReset()` で `_state = State.None` + カウンタ初期化 + `HideAllElements()` +
  `_partyDataList.Clear()`。ギミック終了の action を拾ったら **`OnReset()` を自分で呼ぶ**。

---

## 6. Element の作り方

**JSON エクスポート文字列は 1 度も使っていない (0 回)。** コードで組む。

```csharp
public override void OnSetup()
{
    Controller.RegisterElement("Bait", new Element(0) { tether = true, radius = 3f, thicc = 6f });
    Controller.RegisterElement("BaitObject", new Element(1)
    {
        tether = true,
        refActorComparisonType = 2,
        radius = 0.5f,
        thicc = 6f
    });
    for(var i = 0; i < 8; i++)
        Controller.RegisterElement($"Circle{i}", new Element(1)
            { radius = 5.0f, refActorComparisonType = 2, thicc = 6f, fillIntensity = 0.5f });
}
```

- `OnSetup` では **形だけ**決める。位置・色・表示は実行時に `ApplyElement` で入れる。
- よく使う type は `0` (固定座標) 29 / `1` (アクター相対) 15 / `2` (線) 10。扇はほぼ使わない。
- よく指定するプロパティは `radius` 55 / `thicc` 53 / `tether` 25 / `fillIntensity` 25 /
  `refActorComparisonType` 17 / `color` 10 / `Filled` 8。
- 誘導線 (`tether = true`) を多用する。「そこへ行け」を線で示すのが基本。
- 色は固定せず、`OnUpdate` で点滅させる:

```csharp
public override void OnUpdate()
{
    if(_state == State.None) return;
    if(Controller.TryGetElementByName("Bait", out var el))
        if(el.Enabled) el.color = GradientColor.Get(0xFF00FF00.ToVector4(), 0xFF0000FF.ToVector4()).ToUint();
}
```
緑↔赤のグラデーションが定番。

---

## 7. 自作 API ライブラリ (`#region API`)

**全ファイルに同一内容をコピペしている**(行番号まで一致)。Splatoon スクリプトは 1 ファイル
完結なので、共有ライブラリの代わりにこの region ごと貼る。**中身は編集しない。**
未使用のもの (`GetCorrectionAngle` は 12 本すべてで定義のみ・呼び出し 0) もそのまま残す。

### ジョブ表と Role

```csharp
private static readonly Job[] jobOrder = { DRK, WAR, GNB, PLD, WHM, AST, SCH, SGE,
    DRG, VPR, SAM, MNK, RPR, NIN, BRD, MCH, DNC, RDM, SMN, PCT, BLM };
private static readonly Job[] TankJobs / HealerJobs / MeleeDpsJobs / RangedDpsJobs / MagicDpsJobs;
private static readonly Job[] DpsJobs = MeleeDpsJobs.Concat(RangedDpsJobs).Concat(MagicDpsJobs).ToArray();
private enum Role { Tank, Healer, MeleeDps, RangedDps, MagicDps }
```

### `DirectionCalculator` — 8 方向の中核 (551 回)

```csharp
public enum Direction : int
{
    None = -1, East = 0, SouthEast = 1, South = 2, SouthWest = 3,
    West = 4, NorthWest = 5, North = 6, NorthEast = 7,
}
public enum LR : int { Left = -1, SameOrOpposite = 0, Right = 1 }

static Direction DividePoint(Vector3 pos, float distance, Vector3? center = null)  // 最寄りの 8 方向へ量子化
static Direction GetDirectionFromAngle(Direction dir, int angle)                    // 45 度単位で回す
static Direction GetOppositeDirection(Direction dir)
static LR   GetTwoPointLeftRight(Direction d1, Direction d2)
static int  GetTwoPointAngle(Direction d1, Direction d2)     // -180..180 の 45 度刻み
static float GetAngle(Direction dir)                          // (int)dir * 45
static int  Round45(int value)
```

> **角度規約に注意。** `DirectionCalculator.GetAngle` と `CalculatePositionFromAngle` は
> **East = 0°、南回り (時計回り)** の系である。ECommons の
> `MathHelper.GetRelativeAngle` (North = 0°) とは **90° ずれる**。混ぜないこと。
> 座標生成は必ず `CalculatePositionFromAngle` / `ApplyElement(name, angle, radius)` 経由で行う。
> (座標系一般の話は `ffxiv-coordinates` スキル)

### `ClockDirectionCalculator` — 時計方向 (22 回)

```csharp
var clock = new ClockDirectionCalculator(twelveOClockDirection);
clock.GetDirectionFromClock(3);     // 0 時方向を基準に 3 時方向の Direction
clock.GetClockFromDirection(dir);   // 逆
clock.GetAngle(3);
clock.isValid
```
8 方向しかないので 12 時計位置は `{1,2}→+1`、`{4,5}→+3` のようにマッピングして丸める。

### `ApplyElement` — 表示の唯一の入口 (314 回)

```csharp
private Vector3 BasePosition => new(100, 0, 100);
private Vector3 CalculatePositionFromAngle(float angle, float radius = 0f);
private Vector3 CalculatePositionFromDirection(DirectionCalculator.Direction dir, float radius = 0f);

private void InternalApplyElement(Element e, Vector3 pos, float elementRadius, bool filled, bool tether)
{
    DuoLog.Information($"ApplyElement: {e.Name}, {pos}, ...");
    e.Enabled = true; e.radius = elementRadius; e.tether = tether; e.Filled = filled;
    e.SetRefPosition(pos);
}

// 6 オーバーロード: (Element|string) × (Vector3 | angle+radius | Direction+radius)
ApplyElement("Bait", new Vector3(112f, 0, 85f));
ApplyElement("Bait", angle, 10);
ApplyElement("Bait", DirectionCalculator.Direction.North, 10);
```

呼ぶだけで `Enabled = true` になる。**表示は `ApplyElement`、消去は `HideAllElements`** で統一。

### その他

```csharp
private void HideAllElements() => Controller.GetRegisteredElements().Each(x => x.Value.Enabled = false);
static Vector3 GetExtendedAndClampedPosition(Vector3 center, Vector3 currentPos, float extensionLength, float? limit);
static void ExceptionReturn(string message) => PluginLog.Error(message);
static float GetCorrectionAngle(...);            // 定義のみ・未使用
static float ConvertRotationRadiansToDegrees(float radians);   // rotation → コンパス方位 (度)
static float ConvertDegreesToRotationRadians(float degrees);
private unsafe int GetPlayerTag(uint entityId);  // 一部ファイルのみ
```

---

## 8. トリガの取り方

```csharp
public override void OnStartingCast(uint source, uint castId)
{
    if(castId == 40246)                 // ギミック開始 → データ構築
    {
        SetListEntityIdByJob();
        HideAllElements();
        ShowAkhRhaiReadyGuide(source);
        _state = State.AkhRhai;
    }

    if(_state == State.None) return;    // 開始前は以降を無視

    if(castId == 40227) { _wing = "Left";  HideAllElements(); ShowHalfCutStack(); _state = State.HalfCutStack; }
    if(castId == 40228) { _wing = "Right"; HideAllElements(); ShowHalfCutStack(); _state = State.HalfCutStack; }
}

public override void OnActionEffectEvent(ActionEffectSet set)
{
    if(set.Action == null) return;
    var castId = set.Action.Value.RowId;

    if(castId is 40237 or 40187) HideAllElements();       // 実行された → 消す
    if(castId == 40285) OnReset();                        // ギミック終了 → 全部戻す
}
```

- **詠唱 (`OnStartingCast`) = 予告 → 表示**、**着弾 (`OnActionEffectEvent`) = 実行 → 消去/次へ**
  という役割分担。これが 12 本すべてで共通。
- アクション ID は **裸の数値リテラル**をそのまま書く。名前付き定数にはしない
  (代わりに `// 左翼攻撃` のような日本語コメントを添える)。
- 複数 ID は `castId is 40220 or 40221` のパターンマッチ。
- 短時間の多重発火よけには `_transLock` + `TickScheduler`:

```csharp
_transLock = true;
_ = new TickScheduler(delegate { _transLock = false; }, 1000);
```
`EzThrottler` はほぼ使わない (2 回)。`Controller.Schedule` は 0 回。

---

## 9. 自動化とリプレイ安全分岐

表示だけでなく**キャラを動かす**。`Chat.Instance.ExecuteCommand` を 64 回使用。

| コマンド | 用途 |
|---|---|
| `/mk off <attack>` ほか (計 34) | 頭上マーカーの掃除 |
| `/vnav moveto {x} 0 {z}` | vnavmesh で自動移動 |
| `/vnav stop` | 停止 |
| `/pdrspeed {C.FastCheat}` | 移動速度の切り替え (`OnReset` で既定値に戻す) |

**必ずリプレイ判定で分岐する。** duty recorder 再生中は送信せずログに出すだけ:

```csharp
if(Svc.Condition[ConditionFlag.DutyRecorderPlayback])
    DuoLog.Information($"/vnav moveto 112 0 85");
else
    Chat.Instance.ExecuteCommand($"/vnav moveto 112 0 85");
```

これは録画を回して検証するための仕組み。**自動化を足すときは必ずこの形にする。**

アクション実行も同様に `ActionManager.Instance()` の
`AnimationLock` / `IsRecastTimerActive` を見てから使う (`UseArmsLength()`。ヒーラー・魔法 DPS は
SureCast 7559、それ以外は Arm's Length 7548 と出し分ける)。

同じ位置に繰り返し `/vnav moveto` を送らないよう `_lastVnavPos` で直前の目的地を覚える。

> `Chat.Instance` は ECommons 側で `[Obsolete]`。現行は `Chat.ExecuteCommand(...)` を直接呼ぶ。
> 既存ファイルに合わせるなら `Chat.Instance` のまま、新規なら `Chat.` に寄せてよい。

---

## 10. `OnSettingsDraw` は Debug パネル

ユーザ向けオプションは最小限 (`Master`, `UseFastCheat`, `FastCheat`, `LockFace`, `ArmsLength` 程度)。
本体は畳んだ Debug 表示で、11/12 が `ImGuiEx.CollapsingHeader("Debug")` を使う。

```csharp
public override void OnSettingsDraw()
{
    ImGui.SliderFloat("FastCheat", ref C.FastCheat, 1.0f, 1.5f);

    if(ImGuiEx.CollapsingHeader("Debug"))
    {
        ImGui.Text($"State: {_state}");
        ImGui.Text($"_MineRoleAction: {_mineRoleAction?.Method.Name}");

        List<ImGuiEx.EzTableEntry> Entries = [];
        foreach(var x in _partyDataList)
        {
            Entries.Add(new ImGuiEx.EzTableEntry("Index",    true, () => ImGui.Text(x.Index.ToString())));
            Entries.Add(new ImGuiEx.EzTableEntry("EntityId", true, () => ImGui.Text(x.EntityId.ToString())));
            Entries.Add(new ImGuiEx.EzTableEntry("Name",     true, () => ImGui.Text(x.EntityId.GetObject().Name.ToString())));
            Entries.Add(new ImGuiEx.EzTableEntry("Mine",     true, () => ImGui.Text(x.Mine.ToString())));
            // PartyData のフィールドを機械的に全部並べる
        }
        ImGuiEx.EzTable(Entries);
    }
}
```

**`PartyData` にフィールドを足したら Debug テーブルにも足す**、が習慣になっている。
このテーブルが 8 人分そろっていることが、§4 で全員分を解決している理由そのもの
(自分の表示が出ないとき、誰の解決で崩れたかがこの表で分かる)。

---

## 11. エラー処理とログ

- **`try`/`catch` は 1 つも書かない (0 回)。** ガード節で早期 return する。
- 「起きてはいけない」状況は 1 行にまとめて記録して抜ける:

```csharp
if(sameRoleList.Count() != 3) { ExceptionReturn("sameRoleList.Count() != 3"); return; }
if(myHourGlass == null) { ExceptionReturn("myHourGlass is null"); return; }
```

- 進行の追跡は `DuoLog.Information` (56 回。ゲーム内にも出る)、静かに残すのは `PluginLog` (20 回)。
- null 安全は `?.` / `??` / `TryGetObject` / `TryGetElementByName` を素直に使う。

---

## 12. 書式

- **`if (` / `for (` / `foreach (` と空白を空けて書く。**
  リポジトリ上では `if(` が 1090 : 158 で多数派だが、これは本人の書き方ではない。
  2025-04-15 の公式リポジトリ横断コミット `398eb817 "Auto-refactor"` が 12 ファイルを機械変換した結果で、
  同じコミットが `new List<...>()` → `[]`、`:SplatoonScript` → `: SplatoonScript`、
  `this.EntityId` → `EntityId` も一括適用している。
  **`P2 Diamond Dust Full Toolers.cs` だけこの refactor から漏れており** (`if (` 113 / `if(` 0、
  `for (` 2、`foreach (` 4、`switch (` 1、`this.` 1)、そこに素の書き方が残っている。
  自分のリポジトリで新規に書くなら `if (` 側でよい。
- ローカル変数はほぼ `var`。
- コメントは日本語 567 行 / 英語 656 行。**自作 API の説明は英語、ギミックの説明は日本語**
  という分かれ方をしている (`// 左翼攻撃`, `// リストに８人分の初期インスタンス生成`, `// 1人`)。
- 座標は実測値をそのまま書く (`new Vector3(90.228f, 0, 116.768f)`)。整数に丸めない。
- `switch` 式 (`direction switch { ... }`) をよく使う (84 箇所)。
- コレクション式 `[]` / `[..]` (`_partyDataList = []`, `ValidTerritories => [1238]`)。
  ただしこれも大半は上記 Auto-refactor による変換。素の Diamond Dust では `new()` と `[]` が混在している。

---

## 13. 公式規約との差分 (承知の上で外している点)

新規に書くときも既存に合わせるなら、以下はそのままでよい。公式 (PunishXIV) に PR で出す
ときだけ問題になる。

| 項目 | 現状 | 公式の期待 |
|---|---|---|
| 自機の取得 | `Player.Object` (30 回) | `BasePlayer`。`Player.*` は `BannedSymbols.txt` で RS0030 |
| `Metadata` の型 | `public override Metadata? Metadata` | 基底は非 null。`Metadata?` は CS8764 警告 |
| `Chat.Instance` | 使用 (64 回) | `[Obsolete]`。`Chat.ExecuteCommand` |
| `IEzConfig` | `class Config : IEzConfig` | 実装不要 (`[Obsolete]`) |
| 例外処理 | 無し | — (方針の問題) |
| API region | 全ファイルにコピペ・未使用コードも同梱 | — |

> `Player.Object` を残す場合、ファイル先頭に `#pragma warning disable RS0030` を置けば
> 公式の解析器でも通る。duty replay の Base Player Override は効かなくなる。

---

## 14. 新規スクリプトを書くときの手順

1. `P4 Darklit  Full Toolers.cs` か `P5 Fulgent Blade Full Toolers.cs` を雛形として複製する
   (region 構成が完成形で、かつ大きすぎない)
2. クラス名・namespace・`Metadata`・`ValidTerritories` を書き換える
3. `#region API` は**触らない**
4. `State` enum をギミックの進行に合わせて定義 (`None = 0` 始まり)
5. `PartyData` にそのギミックで確定させたい情報のフィールドを足す
6. `OnSetup` で必要な Element を最小構成で登録 (`Bait` / `BaitObject` / `Circle{i}` が定番)
7. `OnStartingCast` で開始詠唱 → `SetListEntityIdByJob()` → 表示 → `_state` 更新
8. `OnActionEffectEvent` で着弾 → `HideAllElements()` → 次の状態、終了 ID で `OnReset()`
9. 割り当ては `Parse***` で **8 人全員分**を解いて `bool` を返す。件数で整合性を検証し、
    合わなければ `ExceptionReturn` して `return false`
10. `Show***` を private methods に書く。1 行目は `GetMinedata()`、位置指定は必ず `ApplyElement`
11. `OnSettingsDraw` の Debug テーブルに新しいフィールドを追加
12. 自動化を入れるなら `DutyRecorderPlayback` 分岐を必ず付ける
13. `dotnet build RedmoonsScripts/RedmoonsScripts.csproj` で型チェック
14. duty recorder の録画で再生検証 → `Metadata` のバージョンを上げて push
