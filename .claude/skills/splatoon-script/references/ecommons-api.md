# ECommons API

ソース: `Splatoon/ECommons/ECommons/`

Splatoon スクリプトが使う土台ライブラリ。名前空間ごとにまとめる。
公式 340 スクリプトでの import 頻度も併記した (どれを覚えるべきかの目安)。

| 名前空間 | 頻度 | 主な中身 |
|---|---|---|
| `ECommons.DalamudServices` | 258 | `Svc` |
| `ECommons.DalamudServices.Legacy` | 303 | `PrintChat`, `SetTarget`, `AddNotification` |
| `ECommons` | 236 | `GenericHelpers` (拡張メソッド群) |
| `ECommons.ImGuiMethods` | 210 | `ImGuiEx`, `EColor`, `Notify` |
| `ECommons.Configuration` | 182 | `EzConfig` |
| `ECommons.Logging` | 174 | `PluginLog`, `DuoLog` |
| `ECommons.GameFunctions` | 161 | `IGameObject` / `ICharacter` 拡張 |
| `ECommons.MathHelpers` | 129 | `MathHelper` |
| `ECommons.GameHelpers` | 129 | `Player`, `Content`, `Map` |
| `ECommons.Hooks.ActionEffectTypes` | 127 | `ActionEffectSet`, `ActionEffectType` |
| `ECommons.ExcelServices` | 74 | `Job`, territory 定数 |
| `ECommons.Hooks` | 73 | `ActionEffect`, `DirectorUpdate`, `MapEffect`, VFX |
| `ECommons.Throttlers` | 58 | `EzThrottler`, `FrameThrottler` |
| `ECommons.Schedulers` | 44 | `TickScheduler` |
| `ECommons.Automation` | 34 | `Chat`, `TaskManager` |
| `ECommons.PartyFunctions` | 21 | `UniversalParty` |
| `ECommons.EzIpcManager` | 12 | 他プラグイン連携 |

---

## 1. `Svc` — Dalamud サービスの静的アクセス

`ECommons.DalamudServices.Svc`。Dalamud の `[PluginService]` を全部 static で持っている。

スクリプトで実際に使われる上位:

| メンバ | 型 | 使用数 | 用途 |
|---|---|---|---|
| `Svc.Objects` | `IObjectTable` | 419 | オブジェクトテーブル走査。**最頻出** |
| `Svc.ClientState` | `IClientState` | 130 | `TerritoryType`, `LocalPlayer` など |
| `Svc.Condition` | `ICondition` | 116 | `Svc.Condition[ConditionFlag.InCombat]` |
| `Svc.Targets` | `ITargetManager` | 78 | `Target`, `FocusTarget` |
| `Svc.Chat` | `IChatGui` | 45 | ローカルへのメッセージ表示 |
| `Svc.Data` | `IDataManager` | 43 | Excel シート |
| `Svc.Commands` | `ICommandManager` | 24 | コマンド登録 |
| `Svc.GameConfig` | `IGameConfig` | 12 | ゲーム設定の読み書き |
| `Svc.PluginInterface` | `IDalamudPluginInterface` | 10 | 設定ディレクトリなど |

そのほか: `AddonEventManager`, `AddonLifecycle`, `AetheryteList`, `AgentLifecycle`, `Buddies`,
`Console`, `ContextMenu`, `DtrBar`, `DutyState`, `Fates`, `FlyText`, `Framework`, `GameGui`,
`Hook` (`IGameInteropProvider`), `GameInventory`, `GameLifecycle`, `GamepadState`, `Gauges`,
`KeyState`, `MarketBoard`, `NamePlates`, `NotificationManager`, `PfGui`, `Party`, `PlayerState`,
`Log`, `SeStringEvaluator`, `SigScanner`, `Texture`, `TextureSubstitution`, `TextureReadback`,
`TitleScreenMenu`, `Toasts`, `UnlockState`, `ReliableFileStorage`, `GameNetwork` (legacy)。

### `ECommons.DalamudServices.Legacy`

```csharp
void PrintChat(this IChatGui chatGui, XivChatEntry entry)
void SetTarget(this ITargetManager targetManager, IGameObject obj)
void AddNotification(this IUiBuilder builder, string message, string? pluginName = null,
                     NotificationType type = NotificationType.Info, int timeout = 3000)
```

---

## 2. `Player` — 自機ヘルパ

`ECommons.GameHelpers.Player` (static)。

> **Splatoon スクリプトでは原則使わない。** `SplatoonScript.BasePlayer` /
> `Controller.BasePlayer` を使うこと。`Player.Object` / `Player.Character` / `Player.Position`
> 等の大半は公式の `BannedSymbols.txt` で RS0030 として禁止されている
> (duty replay の Base Player Override を壊すため)。
> duty 外で動く Generic スクリプトなら使ってよい。その場合は先頭に
> `#pragma warning disable RS0030` を書く。

主要メンバ (参考):

```csharp
IPlayerCharacter? Object;  Character* Character;  BattleChara* BattleChara;  GameObject* GameObject;
bool Available, Interactable, IsBusy, IsMoving, IsJumping, IsCasting, IsDead, IsAnimationLocked;
bool Mounted, Mounting, CanMount, CanFly, Revivable;
bool IsInDuty, IsOnIsland, IsInPvP, IsPenalised, IsInHomeWorld, IsInHomeDC;
string? Name;  string NameWithWorld;  ulong CID;  StatusList Status;  Sex Sex;
int Level, SyncedLevel;  bool IsLevelSynced;  Number MaxLevel;  int GetLevel(Job job);
RowRef<Race> Race;  RowRef<Tribe> Tribe;  RowRef<World> HomeWorld, CurrentWorld;
RowRef<TerritoryType> Territory;  RowRef<ClassJob> ClassJob;  Job Job;  GrandCompany GrandCompany;
Vector3 Position;  float Rotation;  float AnimationLock;
float DistanceTo(Vector3 | Vector2 | IGameObject other);
string GetNameWithWorld(this IPlayerCharacter? pc);
```

`ECommons.GameHelpers.LegacyPlayer.Player` は旧 API。こちらも全面的に banned。

---

## 3. `ECommons.GameFunctions` — オブジェクト拡張メソッド

`using ECommons.GameFunctions;` を書くと生える。C# 14 の `extension` ブロックで実装されている。

### `IGameObject` に対して

```csharp
GameObject* Struct(this IGameObject o)
GameObject* GameObject(this IEventObj o)
EventObject* Struct(this IEventObj o)
bool IsTargetable(this IGameObject o)
bool IsHostile(this IGameObject a)
NameplateKind GetNameplateKind(this IGameObject o)
bool TryGetPlaceholder(this IGameObject pc, out int number, bool verbose = false)
uint ObjectId          // = EntityId
Vector2 Position2      // = Position.ToVector2()
```

補助: `int GetAttackableEnemyCountAroundPoint(Vector3 point, float radius)`,
`bool TryGetPartyMemberObjectByObjectId(uint objectId, out IGameObject o)`,
`bool TryGetPartyMemberObjectByAddress(IntPtr address, out IGameObject o)`

### `ICharacter` に対して

```csharp
float Health          // CurrentHp / MaxHp
uint  MissingHp
uint  StatusLoop      // StatusLoopVfxId
int   ModelId
Character* Struct()
GameObject* IGameObject()
bool IsCharacterVisible()
byte GetTransformationID()     // 変身 ID。ギミック中の姿判定でよく使う
bool IsInWater()
CombatRole GetRole()           // NonCombat / Tank / Healer / DPS
List<TetherInfo> GetTethers(bool onlySource = false)
```

### `IBattleChara` に対して

```csharp
CastInfo CastInfo
float RemainingCastTime        // TotalCastTime - CurrentCastTime
BattleChara* Struct();  Character* Character();  GameObject* GameObject()

bool IsCasting(uint spellId = 0, ActionType? type = null)
bool IsCasting(params uint[] spellId)
bool IsCasting(IEnumerable<uint> spellId)

bool HasStatus(uint id, float? lessThan = null, float? moreThan = null)
bool HasStatus(uint id, out float time, float? lessThan = null, float? moreThan = null)
bool HasStatus(IEnumerable<uint> id, float? lessThan = null, float? moreThan = null)
bool HasStatus(IEnumerable<uint> id, out List<(uint ID, float Time)> foundStatus, float? lessThan = null, float? moreThan = null)
```

### `TetherInfo`

```csharp
uint Id; bool IsSource; Tether RawInfo; uint PairId; IGameObject? Pair;
```

### ジョブ (`ECommons.ExcelServices`)

```csharp
Job GetJob(this IPlayerCharacter pc)     // ECommons.GameHelpers.LegacyPlayer
Job GetJob(this ClassJob cj)
ClassJob GetData(this Job j)
bool IsTank/IsHealer/IsDps/IsMeleeDps/IsRangedDps/IsPhysicalRangedDps/IsMagicalRangedDps(this Job j)
bool IsCombat/IsDol/IsDoh/IsDom/IsDow(this Job j)
Job GetUpgradedJob(this Job j);  Job GetDowngradedJob(this Job j);  bool IsUpgradeable(this Job j)
int GetIcon(this Job j)
ClassJob? GetJobByName(string) / GetJobByAbbreviation(string) / GetJobById(uint)  (+ TryGet 版)
```

`enum Job : byte` — `ADV=0, GLA, PGL, MRD, LNC, ARC, CNJ, THM, CRP..FSH(18), PLD=19, MNK, WAR,
DRG, BRD, WHM, BLM, ACN, SMN, SCH, ROG, NIN, MCH, DRK, AST, SAM, RDM, BLU, GNB, DNC, RPR, SGE,
VPR=41, PCT=42, BST=43`

`ECommons.ExcelServices.TerritoryEnumeration` に `Raids`, `Trials`, `Dungeons`, `AllianceRaids`,
`VariantDungeons`, `MainCities`, `Inns`, `Houses`, `OpenAreas`, `Prisons`, `ResidentalAreas`,
`TerritoryRegion` の定数クラスがある。`ValidTerritories` に書くとき便利。

---

## 4. `MathHelper`

`ECommons.MathHelpers.MathHelper`。ギミック計算の中核。

### 頻出 (公式スクリプトでの使用数)

```csharp
float GetRelativeAngle(Vector3 origin, Vector3 target)   // 77 回。戻り値は【度】
float GetRelativeAngle(Vector2 origin, Vector2 target)
Vector3 RotateWorldPoint(Vector3 origin, float angle, Vector3 p)   // 53 回。angle は【ラジアン】
Vector2 RotateWorldPoint(Vector2 origin, float angle, Vector2 p)
Vector2 ToVector2(this Vector3 v)     // 264 回。Y を捨てて (X, Z) にする
Vector3 ToVector3(this Vector2 v)     // 130 回
Vector3 ToVector3(this Vector2 v, float Y)
Vector3 SwapYZ(this Vector3 v)
float DegToRad(this float val);  float RadToDeg(this float f)
List<T> EnumerateObjectsClockwise<T>(IEnumerable<T> objects, Func<T,Vector2> getPosition,
                                     Vector2 centerPosition, Vector2 startingPosition)
List<T> EnumerateObjectsClockwise<T>(..., float startingAngle)   // startingAngle は北からの【度】
List<EnumerationResult<T>> EnumerateObjectsClockwiseEx<T>(...)
CardinalDirection GetCardinalDirection(Vector3|Vector2 origin, Vector3|Vector2 target)
CardinalDirection GetCardinalDirection(float angleInDegrees)
```

> **角度の単位に注意**: `GetRelativeAngle` は度を返し、`RotateWorldPoint` はラジアンを取る。
> 間に `DegToRad()` を挟むこと。

### 幾何

```csharp
bool IsPointOnLine(Vector2 point, Vector2 a, Vector2 b, float tolerance = 0f)
Vector2 FindClosestPointOnLine(Vector2 point, Vector2 lineA, Vector2 lineB)
bool IsPointPerpendicularToLineSegment(Vector2 point, Vector2 lineA, Vector2 lineB)
float GetAngleBetweenLines(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)     // ラジアン
float GetAngleBetweenPoints(Vector2 a, Vector2 b)                              // ラジアン
Vector2 MovePoint(Vector2 a, Vector2 b, float distance)      // a から b 方向に distance
Vector2 GetPointFromAngleAndDistance(Vector2 initialPoint, float angle, float distance)
float CalculateDistance(IEnumerable<Vector2> vectors)
float Square(float x)
List<Vector3> CalculateCircularMovement(Vector3 centerPoint, Vector3 initialPoint, Vector3 exitPoint,
    out List<List<Vector3>> candidates, float precision = 36f, int exitPointTolerance = 1,
    (float Min, float Max)? clampRadius = null)
```

### 数値

```csharp
double|float|int Mod(dividend, divisor)          // 常に正の剰余
bool InRange(this <numeric> f, start, end, bool includeEnd = false)
Vector4 Add(this Vector4 v, float i);  Vector4 AddNoW(this Vector4 v, float i)
```

### 色変換 (`ECommons` / `GenericHelpers.ConversionHelpers`)

```csharp
uint ToUint(this Vector4 color)        // ImGui.ColorConvertFloat4ToU32
Vector4 ToVector4(this uint color)     // 185 回。element の color を ImGui に渡すとき
```

---

## 5. `ECommons.Hooks` — 低レベルイベント

Splatoon の override で足りるならそちらを使う。直接使うのは特殊なケースのみ。

| クラス | 提供 |
|---|---|
| `ActionEffect` | `event ActionEffectCallback ActionEffectEvent` (`ActionEffectSet`)、`ActionEffectEntryEvent` |
| `DirectorUpdate` | `Init(Action<DirectorUpdateCategory>)` / 全引数版、`Enable/Disable/Dispose` |
| `MapEffect` | `Init(Action<long, uint, ushort, ushort>)` |
| `ActorVfx` | `ActorVfxCreateEvent(nint vfxPtr, nint vfxPathPtr, nint caster, nint target, float, byte, ushort, byte)`, `ActorVfxDtorEvent` |
| `StaticVfx` | `StaticVfxCreateEvent(nint, string path, string systemSource)`, `StaticVfxRunEvent`, `StaticVfxDtorEvent` |
| `GameObjectCtor` | `GameObjectConstructorCallbackDelegate(nint objectAddress)` |
| `SendAction` | `Init(SendActionCallbackDelegate)` |

### `DirectorUpdateCategory`

```csharp
Commence   = 0x40000001
Recommence = 0x40000006
Complete   = 0x40000003
Wipe       = 0x40000005
```

### `ActionEffectSet` (`ECommons.Hooks.ActionEffectTypes`)

```csharp
Action? Action;  Item? Item;  EventItem? EventItem;  Mount? Mount;
ushort IconId;  string Name;
IGameObject? Target;  IGameObject? Source;  Character? SourceCharacter;
TargetEffect[] TargetEffects;  Vector3 Position;  EffectHeader Header;
Dictionary<ulong, uint> GetSpecificTypeEffect(ActionEffectType type)
```

```csharp
struct TargetEffect {
    ulong TargetID;  EffectEntry this[int index];
    bool GetSpecificTypeEffect(ActionEffectType type, out EffectEntry effect);
    void ForEach(Action<EffectEntry> act);
}
struct EffectEntry {
    ActionEffectType type; byte param0, param1, param2, mult, flags; ushort value;
    byte AttackType;   // param1 & 0xF
    uint Damage;       // mult==0 ? value : value + 65536*mult
}
```

### `ActionEffectType : byte` (抜粋)

`Nothing=0, Miss, FullResist, Damage=3, Heal=4, BlockedDamage, ParriedDamage, Invulnerable,
NoEffectText, MpLoss=10, MpGain, TpLoss, TpGain, ApplyStatusEffectTarget=14,
ApplyStatusEffectSource=15, RecoveredFromStatusEffect=16, LoseStatusEffectTarget=17,
LoseStatusEffectSource=18, StatusNoEffect=20, ThreatPosition=24, EnmityAmountUp=25,
EnmityAmountDown=26, StartActionCombo=27, ComboSucceed=28, Retaliation=29, Knockback=32,
Attract1=33, Attract2=34, Mount=40, FullResistStatus=52, FullResistStatus2=55, VFX=59,
Gauge=60, JobGauge=61, SetModelState=72, SetHP=73, PartialInvulnerable=74, Interrupt=75`

> `Knockback` は 32 と 33 の両方が使われることがある (実装コメントに明記あり)。

---

## 6. スロットラとスケジューラ

```csharp
// 時間ベース
EzThrottler.Throttle(string name, int miliseconds = 500, bool rethrottle = false)  // true なら実行してよい
EzThrottler.Throttle(string name, TimeSpan ts, bool reThrottle = false)
EzThrottler.Check(name);  EzThrottler.Reset(name);  EzThrottler.GetRemainingTime(name, bool allowNegative = false)

// フレームベース
FrameThrottler.Throttle(string name, int frames = 60, bool rethrottle = false)
```

`SplatoonScript` には **スクリプト専用インスタンス** の `EzThrottler` / `FrameThrottler`
(`EzThrottler<string>` / `FrameThrottler<string>`) が生えている。静的版だとほかのスクリプトと
名前が衝突するので、スクリプト内では基本こちらを使う。

```csharp
new TickScheduler(Action function, long delayMS = 0);   // IDisposable
```

Splatoon 側では `Controller.Schedule(action, delayMs)` の方が適切 (Reset で自動キャンセルされる)。

---

## 7. `EzConfig` (`ECommons.Configuration`)

```csharp
T EzConfig.Get<T>();  T EzConfig.Set<T>(T newReference);
string EzConfig.GetPluginConfigDirectory();
T EzConfig.LoadConfiguration<T>(string path, bool createIfMissing = true);
event Action? OnSave;
```

**スクリプトでは直接使わないのが普通**。`Controller.GetConfig<T>()` /
`SplatoonScript<T>.C` がスクリプト用の設定ファイルを面倒みてくれる (保存先は
`{スクリプトパス}.json`、override は `{スクリプトパス}.overrides.json`)。
`Controller.SaveConfig()` で明示保存。

設定クラスは素の POCO でよい (`IEzConfig` の実装は不要 — `[Obsolete]`)。

---

## 8. ロギング

```csharp
PluginLog.Information/Debug/Verbose/Warning/Error/Fatal(string)   // ログのみ
DuoLog  .Information/Debug/Verbose/Warning/Error/Fatal(string)    // ログ + ゲーム内チャット
e.Log(); e.LogWarning(); e.LogDebug(); e.LogDuo();                // Exception 拡張
string e.ToStringFull()
```

デバッグ中は `DuoLog`、常時出力は `PluginLog` を使う。

---

## 9. `ImGuiEx` (`ECommons.ImGuiMethods`)

`OnSettingsDraw` で使う。公式スクリプトでの使用数つき。

```csharp
ImGuiEx.Text(string)                                  // 517
ImGuiEx.Text(Vector4 col, string) / (uint col, string) / (EzColor col, string) / (ImFontPtr, string)
ImGuiEx.TextV(string) / TextV(Vector4? col, string)   //  47 縦位置を揃える
ImGuiEx.TextWrapped(string) / (Vector4? col, string)  //  30
ImGuiEx.TextCopy(string displayText, string? copyText = null)   // 28 クリックでコピー
ImGuiEx.TextCentered(string) / (Vector4 col, string)
ImGuiEx.EnumCombo<T>(string name, ref T field, IDictionary<T,string>? names = null)   // 186
ImGuiEx.EzTable(IEnumerable<EzTableEntry> entries)    //  31
    new EzTableEntry("列名", () => { ... })            // 185
    new EzTableEntry("列名", stretch: true, () => {...})
ImGuiEx.HelpMarker(string helpText, Vector4? color = null, string? symbolOverride = null,
                   bool sameLine = true, bool preserveCursor = false)                 //  82
ImGuiEx.CollapsingHeader(string text) / (Vector4? col, string text)                   //  70
ImGuiEx.TreeNodeCollapsingHeader(string name, Action action, ImGuiTreeNodeFlags extra = None)
ImGuiEx.RadioButtonBool(...)                          //  48
ImGuiEx.CollectionCheckbox<T>(string label, T value, ICollection<T> collection,
                              bool inverted = false, bool delayedOperation = false)   //  33
ImGuiEx.CollectionCheckbox<T>(string label, IEnumerable<T> values, ICollection<T> collection, ...)
ImGuiEx.Checkbox(string label, ref bool value, bool enabled = true)                   //  20
ImGuiEx.Checkbox(string label, ref bool? value)       // 3 状態
ImGuiEx.Tooltip(string)                               //  21
ImGuiEx.BeginDefaultTable(string[] headers, bool drawHeader = true, ImGuiTableFlags extra = None)
ImGuiEx.Vector4FromRGBA(this uint col) / Vector4FromRGB(this uint col, float alpha = 1f)
ImGuiEx.IconButton(FontAwesomeIcon icon, string id = "ECommonsButton", Vector2 size = default, bool enabled = true)
ImGuiEx.IconButtonWithText(FontAwesomeIcon icon, string id, ...)
ImGuiEx.SliderFloat(string label, ref float v, float min, float max[, string format[, ImGuiSliderFlags]])
ImGuiEx.SliderIntAsFloat(string id, ref int value, int min, int max, float divider = 1000)
ImGuiEx.InputTextMultilineExpanding(string id, ref string text, int maxLength = 500, int minLines = 2, int maxLines = 10, int? width = null)
ImGuiEx.SetNextItemFullWidth() / SetNextItemWidthScaled(float)
ImGuiEx.RealtimeDragDrop<T>(...)                      // 並べ替え UI
ImGuiEx.Ctrl                                          // CTRL 押下中か
ImGuiEx.ButtonCtrl(string text, string affix = " (Hold CTRL)")   // 誤爆防止ボタン
ImGuiEx.ColorEdit4(float width, string id, ref uint valueRef, ...)
ImGuiEx.InputInt / InputFloat / InputHex / InputUint / InputListString / InputListUint
```

`EColor` に定数色: `Red`, `RedBright`, `RedDark`, `Green`, `GreenBright`, `GreenDark`,
`Blue`, `BlueBright`, `BlueSky`, `BlueSea`, `White`, `Black`, `Yellow`, `YellowBright`,
`YellowDark`, `Orange`, `OrangeBright`, `Cya`, `CyanBright`, `Violet`, `VioletBright`,
`VioletDark`, `Purple`, `PurpleBright`

`Notify.Info/Success/Warning/Error(string)` でトースト通知。

---

## 10. `UniversalParty` (`ECommons.PartyFunctions`)

cross-world / アライアンスを含めたパーティ取得。

```csharp
static bool IsCrossWorldParty;  static bool IsAlliance;
static int Length;  static int LengthPlayback;
static List<UniversalPartyMember> Members;          // 実プレイ用
static List<UniversalPartyMember> MembersPlayback;  // duty recorder 再生用

class UniversalPartyMember {
    string Name;  RowRef<World> HomeWorld, CurrentWorld;  string NameWithWorld;
    ulong ContentID;  Job ClassJob;  IGameObject IGameObject;   // 見つからないと null
}
```

`Priority` システムはこの `UniversalPartyMember` を返す。`IGameObject` が null になり得るので
必ずチェックする。

---

## 11. `TaskManager` (`ECommons.Automation.NeoTaskManager`)

`Controller.TaskManager` でスクリプト専用インスタンスが取れる (無効化時に自動 dispose)。

```csharp
void Enqueue(Action action, string? taskName = null, TaskManagerConfiguration? config = null)
void Enqueue(Func<bool> func, ...)          // true を返すまで毎フレーム再試行
void Enqueue(Func<bool?> func, ...)         // null = まだ / true = 成功 / false = 失敗
void EnqueueDelay(int ms, bool isFrame = false, TaskManagerConfiguration? config = null)
void InsertDelay(int ms, bool isFrame = false, ...)
void EnqueueMulti(params TaskManagerTask?[] tasks)
void Abort();  void AbortCurrent();  void Step()
bool IsBusy;  int NumQueuedTasks;  float Progress;  long RemainingTimeMS;  bool StepMode
```

```csharp
new TaskManagerConfiguration(int? timeLimitMS = null, bool? abortOnTimeout = null,
    bool? abortOnError = null, bool? timeoutSilently = null, bool? showDebug = null,
    bool? showError = null, bool? executeDefaultConfigurationEvents = null)
```

既定値: `timeLimitMS=30000`, `abortOnTimeout=true`, `abortOnError=true`, `timeoutSilently=false`,
`showDebug=false`, `showError=true`。スクリプト側の既定は
`SplatoonScript.TaskManagerConfiguration` で `new(timeLimitMS: 30000, showDebug: true)`。

---

## 12. `Chat` (`ECommons.Automation`)

```csharp
Chat.ExecuteCommand(string message)        // "/..." をチャット欄経由で実行
Chat.SendMessage(string message)           // 検証つき送信
Chat.SendMessageUnsafe(byte[] message)     // 無検証。通常クライアントが送れない内容も送れてしまう
Chat.ExecuteAction(uint actionId);  Chat.ExecuteGeneralAction(uint generalActionId)
```

`Chat.Instance.<Method>` は `[Obsolete]`。`Chat.<Method>` を直接呼ぶ。

**サーバに届く。** Splatoon 側では `Controller.DangerousEnqueueCommand(text, test)` を使う方が
安全 (リプレイ中は自動で送らない、複数行を分割してキューイングする)。

---

## 13. `GenericHelpers` (`using ECommons;`)

多用される拡張メソッドだけ抜粋。

### コレクション

```csharp
bool AddIfNotExist<T>(this ICollection<T>, T)
bool IsNullOrEmpty<T>(this List<T> | T[] | IEnumerable<T>)
T? FirstOrNull<T>(this IEnumerable<T>[, Func<T,bool>]) where T : struct
T GetRandom<T>(this IEnumerable<T>)
V? SafeSelect<K,V>(this IReadOnlyDictionary<K,V>, K? key[, V defaultValue])
T SafeSelect<T>(this IReadOnlyList<T> | T[], int index)          // 範囲外なら default
T CircularSelect<T>(this IList<T> | T[], int index)              // 剰余でループ
V GetOrDefault<K,V>(this IDictionary<K,V>, K)
V GetOrCreate<K,V>(this IDictionary<K,V>, K[, V | Func<V>])
int IndexOf<T>(this IEnumerable<T>, T | Predicate<T>)
void Each<T>(this IEnumerable<T>, Action<T>)
void EachWithIndex<T>(this IEnumerable<T>, Action<T,int>)
IEnumerable<(T Value,int Index)> WithIndex<T>(this IEnumerable<T>)
bool Toggle<T>(this HashSet<T> | List<T>, T value)
string Print<T>(this IEnumerable<T>, string separator = ", ")
bool TryDequeue<T>(this IList<T>, out T);  T Dequeue<T>(this IList<T>);  T DequeueOrDefault<T>(...)
T[] Together<T>(this T[], params T[])
```

### 汎用

```csharp
bool EqualsAny<T>(this T, params T[])           // element.type.EqualsAny(0,1,4,5) の形で頻出
bool ApproximatelyEquals(this Vector3 a, Vector3 b, float diff = 0.01f)
bool ApproximatelyEquals(this float a, float b, float diff = 0.01f)
bool AddressEquals(this IGameObject obj, IGameObject other)
bool IsTarget(this IGameObject obj)
bool NotNull<T>(this T obj, out T outobj)
bool TryGetValue<T>(this T? nullable, out T value) where T : struct
T JSONClone<T>(this T obj);  T DSFClone<T>(this T obj)
K MaxSafe/MinSafe<T,K>(this IEnumerable<T>, Func<T,K> selector)
uint[] Range(uint inclusiveStart, uint inclusiveEnd)
bool IsOccupied()
bool IsKeyPressed(LimitedKeys key | int key | IEnumerable<...>)
string GetTerritoryName(this Number terr)
```

### 文字列

```csharp
bool EqualsIgnoreCase(this string, string);  bool EqualsIgnoreCaseAny(this string, params string[])
bool ContainsAny(this string, params string[] | IEnumerable<string>[, StringComparison])
bool StartsWithAny(this string, params string[] | IEnumerable<string>[, StringComparison])
string ReplaceFirst(this string text, string search, string replace)
string Cut(this string, int num);  string Repeat(this string, int num);  string Default(this string, string)
string GetText(this SeString | Utf8String | ReadOnlySeString, bool onlyFirst = false)
string? NullWhenEmpty(this string);  bool IsNullOrEmpty(this string?)
```

### Excel (`ECommons.ExcelHelpers`)

```csharp
ExcelSheet<T> GetSheet<T>([ClientLanguage? language])
T? GetRow<T>(uint rowId[, ClientLanguage?]);  bool TryGetRow<T>(uint rowId, out T row)
T? FindRow<T>(Func<T,bool> predicate);  T[] FindRows<T>(Func<T,bool>)
bool TryGetValue<T>(this RowRef<T> rowRef, out T value)
RowRef<T> GetRef(uint id)         // Lumina.Excel.Sheets.<T>.GetRef(id)
```

### Addon (`ECommons.GenericHelpers` / `using ECommons;`)

```csharp
bool TryGetAddonByName<T>(string Addon, out T* AddonPtr) where T : unmanaged
bool TryGetAddonMaster<T>(string addon, out T addonMaster)
bool IsAddonReady(AtkUnitBase* Addon);  bool IsReady(this ref AtkUnitBase Addon)
bool IsScreenReady()
```

---

## 14. `EzIpcManager` (`ECommons.EzIpcManager`)

他プラグイン (WrathCombo, AutoRetainer, VBM/BossMod など) との連携。

```csharp
// 呼ぶ側: フィールドに属性をつけると Init 時に実体が注入される
[EzIPC("PluginInternalName.MethodName", applyPrefix: false)]
public Func<uint, bool> SomeMethod = null!;

// 受ける側のイベント購読
[EzIPCEvent("PluginInternalName.EventName", applyPrefix: false)]
void OnSomething(uint arg) { }

EzIPC.Init(this);                        // prefix なし (IPCName を完全名で書く場合)
EzIPC.Init(this, "MyPluginPrefix");      // prefix つき
```

`EzIPCAttribute(string? iPCName = null, bool applyPrefix = true, Type? actionLastGenericType = null,
SafeWrapper wrapper = SafeWrapper.Inherit)`。
`EzIPC.Init(object instance, string? prefix = null, SafeWrapper safeWrapper = SafeWrapper.None)` は
`EzIPCDisposalToken[]` を返す。詳細は `Splatoon/ECommons/ECommons/EzIpcManager/EzIPC.md`。

公式スクリプトでは `Tests/IPCExample.cs` が参考になる。
