# FFXIVClientStructs

ソース: `Splatoon/FFXIVClientStructs/FFXIVClientStructs/` (`.cs` 約 1074 ファイル)

ゲームクライアントのメモリ構造をそのまま C# の `struct` にマッピングしたライブラリ。
Dalamud の高レベル API (`IGameObject` など) で足りないときだけ降りてくる。

**スクリプトでの立ち位置**: 大半のスクリプトは Dalamud + ECommons で完結する。
FFXIVClientStructs が要るのは「Dalamud が公開していない値」を読むときだけ
(頭上マーカー、変身 ID、アクションのリキャスト、addon の中身など)。

---

## 1. 名前空間の地図

```
FFXIVClientStructs.FFXIV
├─ Client
│  ├─ Enums                       ObjectKind, TerritoryIntendedUse など
│  ├─ Game                        ActionManager, GameMain, InventoryManager, Conditions, ...
│  │  ├─ Character                Character, BattleChara, CastInfo, StatusManager,
│  │  │                           ModelContainer, TimelineContainer, VfxContainer
│  │  ├─ Object                   GameObject, GameObjectManager, EventObject, ...
│  │  ├─ Control                  Control, TargetSystem, CameraManager, EmoteController
│  │  ├─ Event                    EventFramework, EventHandler, ContentDirector, ...
│  │  ├─ Group                    GroupManager, PartyMember
│  │  ├─ Fate                     FateManager
│  │  ├─ Gauge                    ジョブゲージ
│  │  ├─ InstanceContent          InstanceContentDirector 系
│  │  └─ UI                       PlayerState, UIState, MarkingController, ...
│  ├─ UI
│  │  ├─ Agent                    AgentMap, AgentModule, AgentHUD, ...
│  │  ├─ Info                     InfoProxyCrossRealm, InfoProxyPartyMember, ...
│  │  ├─ Arrays                   NumberArrayData, StringArrayData
│  │  └─ Misc                     RaptureHotbarModule, ConfigModule, ...
│  ├─ System/Framework            Framework
│  ├─ Network                     PacketDispatcher など
│  ├─ Graphics                    DrawObject, CharacterBase
│  └─ LayoutEngine                LayoutWorld, InstanceLayout
├─ Component
│  ├─ GUI                         AtkUnitBase, AtkResNode, AtkTextNode, AtkValue …UI 全般
│  ├─ Excel, Exd, Log, Shell, Text
├─ Common
│  ├─ Math                        Vector2/3/4, Matrix (ゲーム側の型)
│  ├─ Configuration
│  └─ Component
└─ Application/Network
```

公式スクリプトでの import 頻度:

| 名前空間 | 回数 |
|---|---|
| `FFXIVClientStructs.FFXIV.Client.Game` | 55 |
| `FFXIVClientStructs.FFXIV.Component.GUI` | 12 |
| `FFXIVClientStructs.FFXIV.Client.UI.Agent` | 7 |
| `FFXIVClientStructs.FFXIV.Client.Game.UI` | 7 |
| `FFXIVClientStructs.FFXIV.Client.Game.Object` | 7 |
| `FFXIVClientStructs.FFXIV.Client.UI.Info` | 5 |
| `FFXIVClientStructs.FFXIV.Client.UI` | 4 |
| `FFXIVClientStructs.FFXIV.Client.System.Framework` | 4 |
| `FFXIVClientStructs.FFXIV.Client.Game.Event` | 4 |
| `FFXIVClientStructs.Interop` | 3 |
| `FFXIVClientStructs.FFXIV.Client.Game.Control` / `.Character` | 3 |

---

## 2. 使い方の作法

### シングルトンは `Instance()`

```csharp
var am  = ActionManager.Instance();       // ActionManager*
var gom = GameObjectManager.Instance();
var mc  = MarkingController.Instance();
```

`[StaticAddress(...)]` 属性でシグネチャ解決される static メソッド。**null が返り得る**ので必ず
チェックする。スクリプトは `unsafe` にする (`public unsafe class MyScript : SplatoonScript`)。

### Dalamud のオブジェクトから構造体へ降りる

ECommons の拡張メソッドを使うのが最短:

```csharp
using ECommons.GameFunctions;

GameObject* go   = someGameObject.Struct();
Character*  chr  = someCharacter.Struct();
BattleChara* bc  = someBattleChara.Struct();
```

素でやるなら `(GameObject*)obj.Address`。

### `FixedSizeArray` / `Span`

`[FixedSizeArray]` の付いた internal フィールドは、InteropGenerator が同名の public な
`Span<T>` プロパティを生成する (`_markers` → `Markers`)。

```csharp
var marker = MarkingController.Instance()->Markers[index];   // Span<GameObjectId>
```

`[FixedSizeArray(isString: true)]` は文字列扱い (`GameObject._name` → `Name`)。

### `Pointer<T>` (`FFXIVClientStructs.Interop`)

オブジェクトテーブル等は `Pointer<T>` の配列になっている。`.Value` で生ポインタを取る。

```csharp
var gom = GameObjectManager.Instance();
for (var i = 0; i < gom->Objects.IndexSorted.Length; i++)
{
    var obj = gom->Objects.IndexSorted[i].Value;   // GameObject*
    if (obj == null) continue;
    ...
}
```

### `[MemberFunction]` / `[VirtualFunction]`

`public partial` メソッドとして宣言されているものは、シグネチャ or 仮想テーブル経由で
実関数を呼ぶ。ゲームに副作用を起こすので、読み取り以外は慎重に。

---

## 3. よく使う構造体

### `GameObject` (`Client.Game.Object`)

```csharp
Vector3 Position;       float Rotation;      float Scale;   float Height;
float   HitboxRadius;   float VfxScale;      Vector3 DrawOffset;
uint    EntityId;       uint  BaseId;        uint  OwnerId;  uint LayoutId;
ushort  ObjectIndex;    ObjectKind ObjectKind;  byte SubKind / BattleNpcSubKind;
byte    Sex;            byte EventState;     byte TargetStatus;
ObjectTargetableFlags TargetableStatus;
byte    YalmDistanceFromPlayerX / Z;
EventId EventId;        ushort FateId;
DrawObject* DrawObject; EventHandler* EventHandler;  LuaActor* LuaActor;
uint    NamePlateIconId;  int RenderFlags;
Vector3 NameplateOffset;  Vector3 CameraOffset;

// 仮想関数
GameObjectId GetGameObjectId();  ObjectKind GetObjectKind();  bool GetIsTargetable();
CStringPointer GetName();  float GetRadius(bool adjustByTransformation = true);
float GetHeight();  byte GetSex();  void EnableDraw();  void DisableDraw();
```

### `Character` (`Client.Game.Character`)

`GameObject` を継承。よく読むフィールド:

```csharp
uint NameId;             // ネームプレート名 ID
ushort CurrentWorld, HomeWorld;
float CastRotation;      // 詠唱時の向き。ギミック方向の判定に使う
byte ModeParam;          // モード依存の付加値
DrawDataContainer DrawData;
TimelineContainer Timeline;      // Timeline.ModelState = 変身 ID
VfxContainer      Vfx;           // Vfx.Tethers = 線の配列
ModelContainer    ModelContainer;// ModelContainer.ModelCharaId
float Alpha;
```

ECommons 経由なら `chr.GetTransformationID()` (= `Timeline.ModelState`)、
`chr.ModelId`、`chr.GetTethers()` で同じ情報が取れる。**そちらを優先する。**

### `BattleChara` (`Client.Game.Character`)

```csharp
StatusManager StatusManager;
CastInfo      CastInfo;             // ActionId, ActionType, CurrentCastTime, TotalCastTime, TargetId, ...
ActionEffectHandler ActionEffectHandler;
ForayInfo     ForayInfo;
```

ECommons 経由: `battleChara.CastInfo`, `battleChara.RemainingCastTime`,
`battleChara.IsCasting(ids)`, `battleChara.HasStatus(id, out time)`。

### `GameObjectManager` (`Client.Game.Object`)

```csharp
static GameObjectManager* Instance();
ObjectArrays Objects;    // Objects.IndexSorted / Objects.CharacterSorted など (Span<Pointer<GameObject>>)
```

通常は `Svc.Objects` (Dalamud の `IObjectTable`) を使う。生テーブルが要るときだけ。

### `MarkingController` (`Client.Game.UI`)

```csharp
static MarkingController* Instance();
Span<GameObjectId> Markers;        // 17 個。頭上マーカー (攻撃1..・アタック等)
Span<uint>         LetterMarkers;  // 26 個
Span<long>         MarkerTimes;    // 17 個
Span<FieldMarker>  FieldMarkers;   // 8 個 (A/B/C/D/1/2/3/4)。Position, X, Y, Z, Active
```

> Splatoon 側に `Splatoon.Memory.Marking.HaveMark(ICharacter obj, uint index)` があり、
> 公式スクリプトでは 33 箇所で使われている。**頭上マーカー判定はこちらを使う。**

### `ActionManager` (`Client.Game`)

```csharp
static ActionManager* Instance();
float GetRecastTime(ActionType actionType, uint actionId);
float GetRecastTimeElapsed(ActionType actionType, uint actionId);
bool  IsRecastTimerActive(ActionType actionType, uint actionId);
bool  IsActionOffCooldown(ActionType actionType, uint actionId);
bool  IsActionTargetInRange(ActionType actionType, uint actionId);
uint  GetActionStatus(ActionType actionType, uint actionId, ulong targetId = 0xE000_0000, ...);
uint  GetAdjustedActionId(uint actionId);
uint  GetCurrentCharges(uint actionId);
float AnimationLock;                                   // Player.AnimationLock の実体
bool  UseAction(ActionType actionType, uint actionId, ulong targetId = 0xE000_0000, ...);
bool  UseActionLocation(ActionType actionType, uint actionId, ulong targetId, Vector3* location, ...);
void  AutoFaceTargetPosition(Vector3* position, ulong followTargetId = 0xE000_0000);
bool  GetGroundPositionForCursor(Vector3* outPosition);
```

`UseAction` 系は**サーバに送る**。表示用スクリプトでは触らない。

### `Framework` (`Client.System.Framework`)

```csharp
static Framework* Instance();   // FrameCounter, 経過時間など
```

### `EventFramework` (`Client.Game.Event`)

```csharp
static EventFramework* Instance();   // GetInstanceContentDirector() などから duty 内部状態を読む
```

### `AgentMap` (`Client.UI.Agent`)

```csharp
static AgentMap* Instance();   // IsPlayerMoving, CurrentMapId, マップマーカーなど
```

### `AtkUnitBase` (`Component.GUI`)

UI addon。ECommons の `TryGetAddonByName<AtkUnitBase>("AddonName", out var addon)` +
`IsAddonReady(addon)` で取り出すのが定石。

---

## 4. 罠

- **フィールドオフセットはパッチごとに変わる。** submodule を更新したら、生オフセットに
  依存したコードは必ず再確認する。可能な限り名前つきフィールド / ECommons 拡張を使い、
  `*(byte*)(addr + 1452)` のような直接オフセットは書かない。
- **`Instance()` は null を返し得る。** ログイン前、ゾーン遷移中など。
- **null ポインタの逆参照はゲームごと落ちる。** `if (ptr == null) return;` を必ず入れる。
- **`Span` の添字は範囲チェックしない。** `Markers[index]` の index 上限は自分で守る。
- **`Character`/`BattleChara` のオフセットは大きい (0x23A0 など)。**
  `(BattleChara*)obj.Address` は対象が本当に BattleChara のときだけ有効。
  `obj is IBattleChara` を先に確認する。
- **Dalamud で取れるものは Dalamud で取る。** `Position` / `Rotation` / `StatusList` /
  `DataId` / `NameId` / `IsCasting` はすべて `IGameObject` / `ICharacter` / `IBattleChara`
  側に生えている。生ポインタに降りるのは最後の手段。
