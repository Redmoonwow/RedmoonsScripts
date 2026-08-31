---
name: ffxiv-coordinates
description: FFXIV の座標系・角度・方向の扱い。ワールド座標 (X/Y/Z の向き)、オブジェクトの Rotation とコンパス方位の変換、Splatoon element の XZY 入れ替え、MathHelper の度/ラジアンの使い分け、マップ座標 (/pos) 換算、ワールド→スクリーン投影、アリーナ幾何 (中心 100,100・N 分割・時計回り列挙)、安置/直線 AoE/扇/ノックバックの判定レシピ。座標や角度の計算・方角判定・位置指定を伴う実装で使う。
---

# FFXIV 座標系

Splatoon スクリプトのバグの多くは座標系・角度・単位の取り違えから来る。
このスキルは実装 (`Splatoon/ECommons/ECommons/MathHelpers/MathHelper.cs`,
`Splatoon/Splatoon/Utility/Utils.cs`, `Splatoon/Splatoon/Serializables/Element.cs`) から
検証した事実だけを載せている。

---

## 1. ワールド座標

`IGameObject.Position` は `System.Numerics.Vector3`。

```
X : 東が +   (East  = +X,  West  = -X)
Y : 上が +   (高さ。平面計算では捨てる)
Z : 南が +   (South = +Z,  North = -Z)
```

**北が -Z** であることが全ての角度計算の前提。

- ほとんどのバトルコンテンツのアリーナ中心は **`(100, y, 100)`**。
  公式スクリプトはこれを決め打ちで書く (`new Vector3(100, 0, 100)`)。
- 平面距離を測るときは Y を落とす:
  ```csharp
  using ECommons.MathHelpers;
  var d = Vector2.Distance(a.Position.ToVector2(), b.Position.ToVector2());
  ```
  `Vector3.ToVector2()` は **Y を捨てて `(X, Z)`** を返す。`Vector2.X` = ワールド X、
  `Vector2.Y` = ワールド **Z**。この `Vector2` を以後「平面座標」と呼ぶ。
- 高さも含めるなら `Vector3.Distance(a.Position, b.Position)`。
  段差のあるアリーナ以外では平面距離で十分。
- 当たり判定は中心間距離ではなくヒットボックスを足す:
  `dist <= radius + obj.HitboxRadius`。

---

## 2. コンパス方位 (bearing)

`MathHelper.GetRelativeAngle(origin, target)` が返すのは **度数のコンパス方位**。

```
  0° = 北 (North, -Z)
 90° = 東 (East,  +X)
180° = 南 (South, +Z)
270° = 西 (West,  -X)
```

つまり **北が 0、時計回りに増える**、範囲 `[0, 360)`。

```csharp
using ECommons.MathHelpers;

var bearing = MathHelper.GetRelativeAngle(center, obj.Position.ToVector2());   // 度
var dir     = MathHelper.GetCardinalDirection(bearing);   // North/East/South/West
```

`GetCardinalDirection(float angle)` の境界 (実装どおり):

| 範囲 | 結果 |
|---|---|
| `[45, 135)` | East |
| `[135, 225)` | South |
| `[225, 315)` | West |
| それ以外 | North |

`Vector3` 版 (`GetRelativeAngle(Vector3, Vector3)`) は内部で `ToVector2()` してから同じ計算を
するので、結果は同じ平面方位。

---

## 3. オブジェクトの `Rotation`

`IGameObject.Rotation` は **ラジアン**、範囲 `[-π, π]`。

```
Rotation =    0     → 南を向いている (South)
Rotation = ±π       → 北 (North)
Rotation = +π/2     → 東 (East)
Rotation = -π/2     → 西 (West)
```

**正方向が反時計回り** (方位が減る向き)。方位との関係は Splatoon の実装
(`Utils.GetRotationWithOverride`) がそのまま示している:

```csharp
// 「obj が position の方を向く」rotation を作る
rotation = (180f - MathHelper.GetRelativeAngle(obj.Position.ToVector2(), position)).DegToRad();
```

逆変換:

```csharp
// rotation から方位 (度) を得る
float BearingOf(float rotationRad) => (180f - rotationRad.RadToDeg() + 360f) % 360f;
```

> `RadToDeg()` は ECommons 実装が `(f * 180/π + 360) % 360` なので、
> **負のラジアンも 0〜360 に正規化される**。素の `f * 180 / MathF.PI` とは挙動が違う。

向いている方向のベクトル (北を基準に反時計回り):

```csharp
var facing = new Vector2(MathF.Sin(obj.Rotation), MathF.Cos(obj.Rotation));   // 平面座標 (X, Z)
// Rotation=0 → (0, 1) = +Z = 南。定義どおり。
```

`Element` 側で向きを扱うフィールド: `includeRotation`, `FaceMe`, `faceplayer`,
`AdditionalRotation`, `RotationOverride*`, `UseCastRotation`
(詠唱時の向き `BattleChara.CastRotation` を使う)。

---

## 4. 回転 — 2 つの `RotatePoint` を混同しない

これが一番踏みやすい罠。

| 関数 | 名前空間 | 回す平面 | 用途 |
|---|---|---|---|
| `MathHelper.RotateWorldPoint(Vector3 origin, float angleRad, Vector3 p)` | `ECommons.MathHelpers` | **X と Z** | ワールド座標をそのまま回す |
| `MathHelper.RotateWorldPoint(Vector2 origin, float angleRad, Vector2 p)` | `ECommons.MathHelpers` | X と Y (= 平面座標) | `ToVector2()` した座標を回す |
| `Utils.RotatePoint(Vector3 origin, float angleRad, Vector3 point)` | `Splatoon.Utility` | **X と Y** | **Splatoon element 空間** (XZY 入れ替え済み) 用 |
| `Utils.RotatePoint(float cx, float cy, float angleRad, Vector3 p)` | `Splatoon.Utility` | X と Y | 同上 |

**ワールド座標の `Vector3` を回すなら `MathHelper.RotateWorldPoint`。**
`Utils.RotatePoint` に生のワールド `Vector3` を渡すと X と高さを回してしまう。

角度は **すべてラジアン**。度で持っているなら `.DegToRad()` を挟む。

```csharp
using ECommons.MathHelpers;

// (100,0,100) を中心に、真北 8m の点を時計回りに 45° ずつ回して 8 方向の塔を作る
var center = new Vector3(100, 0, 100);
var north  = new Vector3(100, 0, 92);      // 北は -Z なので Z を減らす
var towers = new Dictionary<int, Vector2>();
for (var i = 0; i < 8; i++)
    towers[i] = MathHelper.RotateWorldPoint(center, (45f * i).DegToRad(), north).ToVector2();
```

回転の向き: `RotateWorldPoint` の正角度は **平面上で時計回り** (方位が増える向き)。
上の例で `i=2` (90°) は東になる。オブジェクトの `Rotation` (反時計回りが正) とは
向きが逆なので、両方を混ぜるときは符号に注意する。

---

## 5. Splatoon element の座標空間

`Element` の座標フィールドは **Y と Z が入れ替わっている**。

```
element.refX = world.X
element.refY = world.Z     ← 高さではない
element.refZ = world.Y     ← 高さ
```

`offX/offY/offZ` も同じ。素直に書けるアクセサが 3 つある:

```csharp
using Splatoon.SplatoonScripting;   // Extensions

// 推奨: ワールド座標でそのまま読み書きできる
e.RefPosition = worldPos;              // Element のプロパティ (get も可)
var p = e.RefPosition;                 // → new(refX, refZ, refY)

e.SetRefPosition(worldPos);            // 拡張メソッド。RefPosition の setter と同じ
e.SetOffPosition(worldPos);

e.RefPositionXZY                       // 生の element 空間 (refX, refY, refZ)
```

ワールド `Vector3` ↔ element 空間の変換ヘルパ:

```csharp
using Splatoon.Utility;
Utils.XZY(v)                    // Y と Z を入れ替えた Vector3 を返す
obj.GetPositionXZY()            // IGameObject の Position を入れ替えて返す
```

**原則: `refX/refY/refZ` を直接触らず `RefPosition` / `SetRefPosition` を使う。**
直接触るのは、既存の element JSON をコード生成するときくらい。

---

## 6. 単位と型の早見表

| 対象 | 単位 | 範囲 | 備考 |
|---|---|---|---|
| `IGameObject.Rotation` | ラジアン | `[-π, π]` | 0 = 南、正 = 反時計回り |
| `MathHelper.GetRelativeAngle` | **度** | `[0, 360)` | 0 = 北、時計回り |
| `MathHelper.RotateWorldPoint` の `angle` | **ラジアン** | — | 正 = 時計回り |
| `MathHelper.GetAngleBetweenPoints` | ラジアン | — | `atan2` そのまま |
| `MathHelper.GetAngleBetweenLines` | ラジアン | — | |
| `Element.coneAngleMin/Max` | 度 | — | `int`。向き基準の相対角 |
| `Element.AdditionalRotation` | ラジアン | — | |
| `Element.RotationMin/Max` | 度 | — | `LimitRotation` 用 |

変換:

```csharp
using ECommons.MathHelpers;
float rad = deg.DegToRad();     // (float)(MathF.PI / 180f * val)
float deg = rad.RadToDeg();     // (f * 180/π + 360) % 360  ← 0〜360 に正規化される

// BCL 版 (公式スクリプトでも使われる)。正規化されない点だけ違う
float rad2 = float.DegreesToRadians(deg);
float deg2 = float.RadiansToDegrees(rad);
```

---

## 7. マップ座標 (`/pos` に出る数字)

ワールド座標とは別物。変換には `Map` シートの `SizeFactor` / `OffsetX` / `OffsetY` が要る。
**ECommons にワールド→マップの関数はない** (逆方向のピクセル→ワールドだけある) ので自前で書く。

```csharp
using ECommons;            // GetRow<T>
using Lumina.Excel.Sheets;

// ワールド座標 → マップ座標
static float WorldToMap(float worldCoord, ushort offset, ushort sizeFactor)
{
    var c = sizeFactor / 100f;
    var scaled = (worldCoord + offset) * c;
    return (41f / c) * ((scaled + 1024f) / 2048f) + 1f;
}

var map = GetRow<Map>(mapRowId)!.Value;
var mapX = WorldToMap(pos.X, (ushort)map.OffsetX, map.SizeFactor);
var mapY = WorldToMap(pos.Z, (ushort)map.OffsetY, map.SizeFactor);   // Z が縦
```

逆 (マップのピクセル座標 → ワールド) は ECommons にある:

```csharp
using ECommons.GameHelpers;
Vector3 Map.PixelCoordsToWorldCoords(int x, int z, uint mapId)
float   Map.PixelCoordToWorldCoord(float coord, float scale, short offset)
```

一次資料: <https://github.com/xivapi/ffxiv-datamining/blob/master/docs/MapCoordinates.md>

> ギミックスクリプトでマップ座標が要ることはほぼない。チャットに座標を出す等の用途だけ。

---

## 8. スクリーン投影

```csharp
using ECommons.DalamudServices;

if (Svc.GameGui.WorldToScreen(worldPos, out Vector2 screenPos))
{
    // screenPos はピクセル。画面外だと false
}
```

Splatoon 内部も `Utils.WorldToScreen` で最終的にこれを呼んでいる。
ImGui のフォアグラウンド描画に使う:

```csharp
using Dalamud.Bindings.ImGui;
ImGui.GetForegroundDrawList().AddCircleFilled(screenPos, 8f, 0xFF0000FF);
```

ただし **Splatoon で描くなら element に任せる方が正しい** (レンダエンジン・射影補正・
ユーザ設定が全部効く)。生の drawlist を使うのは element で表現できないものだけ。

### カメラ角度

`Splatoon.Memory.Camera` は `internal` なのでスクリプトからは見えない。
画面基準の方向表示が要る場合、公式スクリプトはシグネチャスキャンで自前の `Camera` を
各スクリプト内に定義している (`SplatoonScripts/Generic/ForceSetDirection.cs`)。
パッチで壊れやすいので、可能なら `WorldToScreen` で済ませる。

---

## 9. レシピ

### N 分割のスロット番号を求める

```csharp
// 中心から見て北を 0 として時計回りに 8 分割したとき、対象がどのスロットか
static int SlotOf(Vector2 center, Vector2 p, int divisions)
{
    var bearing = MathHelper.GetRelativeAngle(center, p);
    var step = 360f / divisions;
    return (int)MathF.Floor(((bearing + step / 2f) % 360f) / step);
}
```

`+ step/2` は「北ちょうど」がスロット 0 の中心に来るようにするため。
四隅基準 (北東を 0) にしたいときは足さない。

### 時計回りに並べる

```csharp
var ordered = MathHelper.EnumerateObjectsClockwise(
    objects, x => x.Position.ToVector2(), center, startingPosition);

// 開始角を度で指定する版 (北からの度)
var ordered2 = MathHelper.EnumerateObjectsClockwise(
    objects, x => x.Position.ToVector2(), center, 0f);

// 元の並び順つきが欲しいなら Ex 版
var ex = MathHelper.EnumerateObjectsClockwiseEx(objects, ..., center, 0f);
```

### 対象からの相対方向 (左右・前後)

```csharp
// obj から見て自分が左か右か
var toMe    = MathHelper.GetRelativeAngle(obj.Position.ToVector2(), BasePlayer.Position.ToVector2());
var facing  = (180f - obj.Rotation.RadToDeg() + 360f) % 360f;   // obj の向いている方位
var rel     = (toMe - facing + 540f) % 360f - 180f;             // -180..180
// rel > 0 なら右、< 0 なら左、|rel| が小さいなら正面
```

### 直線 AoE に当たるか

```csharp
// a→b の線分に対する垂線が線分内に落ちるか + 距離が幅以内か
var p = BasePlayer.Position.ToVector2();
if (MathHelper.IsPointPerpendicularToLineSegment(p, a, b))
{
    var closest = MathHelper.FindClosestPointOnLine(p, a, b);
    var hit = Vector2.Distance(p, closest) <= halfWidth;
}
```

### 扇に入っているか

```csharp
var bearing = MathHelper.GetRelativeAngle(sourcePos, myPos);
var facing  = (180f - source.Rotation.RadToDeg() + 360f) % 360f;
var diff    = MathF.Abs((bearing - facing + 540f) % 360f - 180f);
var inCone  = diff <= halfAngleDegrees && Vector2.Distance(sourcePos, myPos) <= range;
```

element で描くなら `type: 4` (オブジェクト相対の扇) + `coneAngleMin/Max` +
`includeRotation: true` を使う方が確実。

### ノックバック後の位置

```csharp
// source から distance だけ押された先
var from = source.Position.ToVector2();
var me   = BasePlayer.Position.ToVector2();
var after = MathHelper.MovePoint(from, me, Vector2.Distance(from, me) + distance);
// 円形アリーナの外に出るか
var willFall = Vector2.Distance(center, after) > arenaRadius;
```

`MovePoint(a, b, distance)` は「a から b の方向に distance 進んだ点」を返す。

### 安置 (使われていないスロット) を求める

```csharp
var used = dangerousObjects.Select(x => SlotOf(center, x.Position.ToVector2(), 8)).ToHashSet();
var safe = Enumerable.Range(0, 8).Where(i => !used.Contains(i)).ToList();
var safePos = MathHelper.RotateWorldPoint(center3, (45f * safe[0]).DegToRad(), north3);
```

### 円周上を移動する経路

```csharp
var path = MathHelper.CalculateCircularMovement(
    centerPoint: center3, initialPoint: myPos3, exitPoint: goal3,
    out var candidates, precision: 36f, exitPointTolerance: 1, clampRadius: (8f, 12f));
```

---

## 10. チェックリスト

座標まわりの実装をレビューするとき、この順に見る。

1. 角度の単位は合っているか (度を取る関数にラジアンを渡していないか)
2. `RotateWorldPoint` と `Utils.RotatePoint` を取り違えていないか
3. element に座標を入れるとき `RefPosition` / `SetRefPosition` を使っているか
   (`refY` に高さを入れていないか)
4. 距離判定でヒットボックスを足しているか
5. 北 = `-Z` を前提にしているか (`+Z` を北だと思っていないか)
6. `RadToDeg()` が 0〜360 に正規化することを踏まえているか
7. 方位 (時計回り) とオブジェクト Rotation (反時計回り) の符号を混ぜていないか
8. アリーナ中心を `(100, 0, 100)` と決め打ちしてよいコンテンツか
   (そうでないアリーナもある。ボスの初期位置や床オブジェクトから取る方が安全)
