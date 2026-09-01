# Splatoon スクリプト API

ソース: `Splatoon/Splatoon/SplatoonScripting/`, `Splatoon/Splatoon/Serializables/`

> **基準バージョン**: Splatoon `d5017695` (2026-08-27) と、そこに pin されている
> ECommons `cd1a88da` (2026-07-01) / FFXIVClientStructs `0769d1f1` / Dalamud dev libs。
> スクリプトは Splatoon が同梱する版に対してコンパイルされるので、**上流の最新ではなく
> この pin が正**。`git submodule status` が上記と違っていたら、この文書を再検証すること。

---

## 1. `SplatoonScript` 基底クラス

`Splatoon/Splatoon/SplatoonScripting/SplatoonScript.cs`

```csharp
public abstract class SplatoonScript<T> : SplatoonScript where T : new()
{
    public T C => Controller.GetConfig<T>();   // 設定へのショートカット
}
public abstract class SplatoonScript { ... }
```

`SplatoonScript<TConfig>` を継承すると `C` で設定にアクセスできる。設定不要なら素の
`SplatoonScript` を継承する。

### 必須メンバ

| メンバ | 説明 |
|---|---|
| `abstract Metadata Metadata` | バージョン・作者などのメタ情報。**必須** |
| `abstract HashSet<uint>? ValidTerritories` | 動作する territory。`[]`=全域 / `null`=ログアウト中も動作 |

### プロパティ

| メンバ | 型 | 説明 |
|---|---|---|
| `BasePlayer` | `IPlayerCharacter` | 自機。duty replay の Base Player Override を尊重する。**自機取得はこれを使う** |
| `Controller` | `Controller` | ヘルパ本体 (下記) |
| `EzThrottler` | `EzThrottler<string>` | このスクリプト専用の時間スロットラ (遅延生成) |
| `FrameThrottler` | `FrameThrottler<string>` | このスクリプト専用のフレームスロットラ |
| `IsEnabled` | `bool` | 現在有効か |
| `InternalData` | `InternalData` | `Path` / `Namespace` / `Name` / `FullName` / `GUID` など |
| `virtual Dictionary<int,string>? Changelog` | | 更新時にユーザへ表示される変更履歴 |
| `virtual bool Safe` | | Splatoon/ECommons/Dalamud API のみで完結しているかの自己申告 |
| `virtual TaskManagerConfiguration TaskManagerConfiguration` | | 既定 `new(timeLimitMS: 30000, showDebug: true)` |

### メソッド

`Loc(en, jp, de, fr, cn)` — 現在のゲーム言語に応じた文字列を返す。未定義なら最初に定義されたもの。

### ライフサイクル override

| override | 呼ばれるタイミング |
|---|---|
| `OnSetup()` | コンパイル・ロード直後に **1 度だけ**。element / layout の登録専用。**フックや後始末が要るものを置かない** |
| `OnEnable()` | 対象 territory に入ったとき。フック登録・イベント購読はここ |
| `OnDisable()` | 対象 territory を出たとき。購読解除はここ |
| `OnReset()` | director update (commence / recommence / wipe)、戦闘開始、戦闘終了、無効化の **直前**。有効化の **直後** にも呼ばれる。クリーンアップはここ |
| `OnUpdate()` | 毎フレーム |
| `OnScriptUpdated(uint previousVersion)` | スクリプト更新時。`previousVersion` は強制更新だと同値のこともある |
| `OnSettingsDraw()` | 設定 UI。override すると設定セクションが自動で生える |

### 戦闘・進行イベント

| override | 内容 |
|---|---|
| `OnCombatStart()` / `OnCombatEnd()` | 戦闘開始 / 終了 |
| `OnPhaseChange(int newPhase)` | フェーズ変更。ユーザ手動変更でも呼ばれる |
| `OnDirectorUpdate(DirectorUpdateCategory category)` | duty の commence / recommence / complete / wipe |
| `OnDirectorUpdate(nint directorPtr, uint targetId, DirectorUpdateCategory a3, uint a4, uint a5, int a6, int a7, int a8, int a9)` | 全引数版 |
| `OnMessage(string Message)` | layout のトリガシステムと同じメッセージ |

### ギミック検知イベント

| override | 内容 |
|---|---|
| `OnStartingCast(uint source, uint castId)` | 敵の詠唱開始 |
| `OnStartingCast(uint sourceId, PacketActorCast* packet)` | 同上・パケット生 |
| `OnMapEffect(uint position, ushort data1, ushort data2)` | map effect。`position` は地図座標とは無関係の内部インデックス |
| `OnObjectEffect(uint target, uint entityId, uint actionId)` | object effect |
| `OnVFXSpawn(uint target, string vfxPath)` | 対象に VFX が発生 |
| `OnTetherCreate(uint source, uint target, uint data2, uint data3, uint data5)` | 線が張られた |
| `OnTetherRemoval(uint source, uint data2, uint data3, uint data5)` | 線が消えた |
| `OnGainBuffEffect(uint sourceId, Status Status)` | バフ / デバフ付与 |
| `OnRemoveBuffEffect(uint sourceId, Status Status)` | 解除 |
| `OnUpdateBuffEffect(uint sourceId, Status status)` | 更新 |
| `OnObjectCreation(nint newObjectPtr)` | オブジェクト生成直後 |
| `OnActionEffect(uint ActionID, ushort animationID, ActionEffectType type, uint sourceID, ulong targetOID, uint damage)` | アクション効果 (簡易) |
| `OnActionEffectEvent(ActionEffectSet set)` | アクション効果 (完全)。`set.Action`, `set.Source`, `set.Target`, `set.TargetEffects` |
| `OnActorControl(uint sourceId, uint command, uint p1..p8, ulong targetId, byte replaying)` | **VOLATILE**。毎パケット。重い処理を書かない |

> シグネチャは Splatoon 更新で変わる。`CS0115` が出たら
> `Splatoon/Splatoon/SplatoonScripting/SplatoonScript.cs` の現在の宣言を確認する。

---

## 2. `Metadata`

```csharp
public override Metadata Metadata { get; } = new(version, author, description, website);
```

コンストラクタ: `(uint version)` / `(uint, string? author)` / `(uint, string?, string? description)` /
`(uint, string?, string?, string? website)`。
`UpdateURL` / `BlacklistURL` は `[Obsolete]` — 未実装なので触らない。

update.csv 生成器が版数を拾う正規表現は `override.+Metadata.+Metadata.+new\D+([0-9]+)` なので、
`public override Metadata Metadata => new(3, "Redmoon");` の形を崩さないこと。

---

## 3. `Controller`

`Splatoon/Splatoon/SplatoonScripting/Controller.cs`

### 状態

| メンバ | 説明 |
|---|---|
| `BasePlayer` | 自機 (override 対応) |
| `InCombat` | `Svc.Condition[ConditionFlag.InCombat]` |
| `Phase` | 現在フェーズ |
| `CombatSeconds` / `CombatMiliseconds` | 戦闘開始からの経過。非戦闘時は `-1` |
| `Scene` | 現在のシーン ID |
| `AttentionColor` | 注意色 (uint)。element の色に使うと統一感が出る |
| `RolePosition` | 自分のロール位置 (T1/T2/H1/H2/M1/M2/R1/R2) |
| `TaskManager` | スクリプト専用 NeoTaskManager (遅延生成・無効化時に dispose) |
| `Plugin` | Splatoon 本体インスタンス |

### 設定

```csharp
T GetConfig<T>() where T : new()    // ロード済みなら再利用
void SaveConfig()
```

`SplatoonScript<T>` を継承していれば `C` が `GetConfig<T>()` のエイリアス。

### Element 登録 (最頻出)

```csharp
// エクスポート文字列から (公式の標準的なやり方)
void   RegisterElementFromCode(string UniqueName, string ExportString, bool overwrite = false)
Element RegisterElementFromCode(string UniqueName, string ExportString, bool overwrite = false)
void   RegisterElementFromCode(string ExportString, bool overwrite = false)          // 名前は element 名
void   RegisterElementsFromMultilineCode(string ExportStringPerLine, bool overwrite = false)
bool   TryRegisterElementFromCode(string UniqueName, string ExportString, out Element? element, bool overwrite = false)
bool   TryRegisterElementFromCode(string ExportString, out Element? element, bool overwrite = false)

// オブジェクトから
void RegisterElement(string UniqueName, Element element, bool overwrite = false)
bool TryRegisterElement(string UniqueName, Element element, bool overwrite = false)

// 取得・削除
bool     TryGetElementByName(string name, out Element? element)
Element? GetElementByName(string name)
bool     TryUnregisterElement(string name)
ReadOnlyDictionary<string, Element> GetRegisteredElements()
void     ClearRegisteredElements()
ReadOnlyDictionary<string, Element> OriginalElements   // OnSetup 時点の未編集コピー (参照専用)
```

### Layout 登録

```csharp
bool TryRegisterLayoutFromCode(string UniqueName, string ExportString, out Layout? layout, bool overwrite = false)
bool TryRegisterLayoutFromCode(string ExportString, out Layout? layout, bool overwrite = false)
bool TryRegisterLayout(string UniqueName, Layout layout, bool overwrite = false)
void RegisterLayoutFromCode(string key, string code)
bool TryGetLayoutByName(string name, out Layout? layout)
bool TryUnregisterLayout(string name)
ReadOnlyDictionary<string, Layout> GetRegisteredLayouts()
void ClearRegisteredLayouts()
ReadOnlyDictionary<string, Layout> OriginalLayouts
void Clear()                                  // element も layout も全消去
void Hide(bool elements = true, bool layouts = true)
```

ユーザは登録済み element / layout を GUI で編集でき、その差分は override として保存される
(`ApplyOverrides` / `SaveOverrides` が内部で処理する)。だから **登録名を変えると設定が飛ぶ**。

### スケジューリング

```csharp
void Schedule(Action action, int delayMs)     // Reset 時に自動キャンセル
void CancelSchedulers()
void ScheduleReset(uint delayMs = uint.MaxValue)
void Reset()                                  // OnReset + 追加クリーンアップ
```

### パーティ

```csharp
IEnumerable<IPlayerCharacter> GetPartyMembers()   // 非 cross-world 限定。duty recorder 対応
```

cross-world / アライアンスを含めるなら ECommons の `UniversalParty.Members` を使う。

### Attention Window (画面中央の大きい指示表示)

```csharp
void DisplayAttentionWindowLine(string text)
void DisplayAttentionWindowLine(string text, params string[] arguments)          // $1, $2 ... で展開
void DisplayAttentionWindowLine(Vector4? color, string text, params string[] arguments)
void DisplayAttentionWindowLine(Action action)
void DisplayAttentionWindowRaw(Action action)
```

**毎フレーム呼び続けないと閉じる**。渡した `Action` は同一フレームでは呼ばれず、複数回呼ばれる
可能性があるので副作用を入れない。

### Map effect

```csharp
uint GetMapEffect(uint mapEffectIndex)
uint GetMapEffect<T>(T mapEffect) where T : Enum
```

### コマンド送信 (サーバに届く。取り扱い注意)

```csharp
void DangerousEnqueueCommand(string text, bool test)   // test=true なら送信しない。リプレイ中は自動で送らない
void CancelQueuedCommands()
```

---

## 4. `Element`

`Splatoon/Splatoon/Serializables/Element.cs`

### `type` (コンストラクタ引数)

| 値 | 意味 |
|---|---|
| 0 | 固定座標のオブジェクト |
| 1 | アクター相対のオブジェクト |
| 2 | 2 つの固定座標を結ぶ線 |
| 3 | オブジェクト相対の線 |
| 4 | オブジェクト相対の扇 |
| 5 | 固定座標の扇 |

### 座標

`refX / refY / refZ`、`offX / offY / offZ`。**`refY` はゲーム座標の Z、`refZ` が Y (高さ)。**
`element.SetRefPosition(vector3)` / `SetOffPosition(vector3)` を使うと自動変換される。
読み出しは `element.RefPosition` (Vector3) / `RefPositionXZY`。

### 形状・見た目

`radius`, `Donut`, `coneAngleMin/Max`, `color` (AABBGGRR uint), `Filled`, `fillIntensity`,
`overrideFillColor` + `originFillColor` / `endFillColor`, `thicc`, `FillStep`, `LegacyFill`,
`RenderEngineKind`, `Nodraw`, `Enabled`

`castAnimation` (`CastAnimationKind`: `Unspecified` / `Pulse` / `ColorShift` / `Fill`) と
`animationColor`, `pulseSize`, `pulseFrequency`。

`mechanicType` (`MechanicType`: `Unspecified` / `Danger` / `Safe` / `Soak` / `Gaze` / `Knockback` /
`Information`) を設定するとユーザのテーマ色が自動適用される。

### テキスト

`overlayText`, `overlayTextIntl`, `overlayTextColor`, `overlayBGColor`, `overlayVOffset`,
`overlayFScale`, `overlayPlaceholders`

### 対象の絞り込み — `refActorComparisonType`

| 値 | 比較対象 | 対応フィールド |
|---|---|---|
| 0 | 名前 | `refActorName`, `refActorNameIntl` |
| 1 | Model ID | `refActorModelID` |
| 2 | Object ID | `refActorObjectID` |
| 3 | Data ID | `refActorDataID` |
| 4 | NPC ID | `refActorNPCID` |
| 5 | プレースホルダ | `refActorPlaceholder` |
| 6 | Name ID | `refActorNPCNameID` |
| 7 | VFX パス | `refActorVFXPath`, `refActorVFXMin/Max` |
| 8 | Object effect | `refActorObjectEffectData1/2`, `Min/Max`, `LastOnly` |
| 9 | ネームプレートアイコン ID | `refActorNamePlateIconID` |

`refActorComparisonAnd = true` で複数条件の AND。

`refActorType`: 0 = 指定名のオブジェクト / 1 = 自分 / 2 = ターゲット中の敵

### そのほかの条件

- 詠唱: `refActorRequireCast`, `refActorCastId` (List), `refActorCastReverse`,
  `refActorUseCastTime` + `refActorCastTimeMin/Max`, `refActorUseOvercast`
- バフ: `refActorRequireBuff`, `refActorBuffId` (List), `refActorRequireAllBuffs`,
  `refActorRequireBuffsInvert`, `refActorUseBuffTime` + `Min/Max`, `refActorUseBuffParam` + `refActorBuffParam`
- 線: `refActorTether`, `refActorTetherTimeMin/Max`, `refActorTetherParam1/2/3`,
  `refActorIsTetherSource`, `refActorIsTetherInvert`, `refActorIsTetherLive`,
  `refActorTetherConnectedWithPlayer`
- 距離: `LimitDistance` (+ `Invert`), `DistanceMin/Max`, `DistanceSourceX/Y/Z`,
  `UseDistanceSourcePlaceholder` + `DistanceSourcePlaceholder`
- 角度: `LimitRotation`, `RotationMin/Max`
- 生存期間: `refActorObjectLife`, `refActorLifetimeMin/Max`
- 状態: `onlyTargetable`, `onlyUnTargetable`, `onlyVisible`, `IsDead`,
  `UseHitboxRadius` + `HitboxRadiusMin/Max`, `ObjectKinds`
- 変身: `refActorUseTransformation`, `refActorTransformationID`
- マーカー: `refMark`, `refMarkID`
- ターゲット変換: `TargetAlteration` (`None` / `Tethered` / `Targeted` /
  `Closest_Player_1..4` / `Furthest_Player_1..4`)
- 向き: `FaceMe`, `faceplayer` (既定 `"<1>"`), `FaceInvert`, `includeRotation`,
  `AdditionalRotation`, `RotationOverride` 一式
- 列挙: `Enumeration` (`None` / `Clockwise` / `Counter_Clockwise`), `EnumerationOrder`,
  `EnumerationCenter`, `EnumerationStart`
- 線の端: `tether`, `ExtraTetherLength`, `LineEndA/B` (`None` / `Arrow`), `EnablePointerLine` + `PointerLineStyle`
- ヒットボックス加算: `includeHitbox`, `includeOwnHitbox`, `LineAddHitboxLength*`, `LineAddPlayerHitboxLength*`
- map effect: `MapEffects` (List<MapEffectData>), `MapEffectInvert`, `MapEffectAnd`
- 詠唱由来の値を使う: `UseCastRotation`, `UseCastPosition`, `UseCastTarget`
- 条件付き表示: `Conditional`, `ConditionalInvert`, `ConditionalReset`
- プレースホルダ座標: `UsePlaceholderAsRefPosition` / `OffPosition` + `PlaceholdersRefPosition` / `PlaceholdersOffPosition`, `PairingMode` (`One_to_one` / `Every_to_every`)

### 拡張メソッド (`Splatoon.SplatoonScripting.Extensions`)

```csharp
IGameObject? GetObject(this uint objectID)
bool TryGetObject(this uint objectID, out IGameObject? obj)
bool TryGetBattleNpc(this uint objectID, out IBattleNpc? obj)
bool TryGetPlayer(this uint objectID, out IPlayerCharacter? obj)
void SetRefPosition(this Element e, Vector3 Position)
void SetOffPosition(this Element e, Vector3 Position)
string GetUniqueId(this Element e, IGameObject? maybeGameObject = null)
```

---

## 5. `Layout`

`Splatoon/Splatoon/Serializables/Layout.cs`

element をまとめて条件付き表示する入れ物。

| フィールド | 説明 |
|---|---|
| `Enabled`, `Name`, `Description`, `Group` | 基本 |
| `ElementsL` | `List<Element>` |
| `ZoneLockH` (`HashSet<ushort>`), `IsZoneBlacklist` | ゾーン制限 |
| `Scenes` (`HashSet<int>`) | シーン制限 |
| `JobLockH` (`HashSet<Job>`) | ジョブ制限 |
| `DCond` | 0=常時 / 1=戦闘中のみ / 2=インスタンス内のみ / 3=戦闘 AND インスタンス / 4=戦闘 OR インスタンス / 5=非表示 / 6=非戦闘時 / 7=インスタンス外 / 8=非戦闘 AND 非インスタンス / 9=非戦闘 OR 非インスタンス |
| `Phase` | フェーズ制限 |
| `UseTriggers`, `Triggers` (`List<Trigger>`) | トリガ |
| `UseDistanceLimit`, `MinDistance`, `MaxDistance`, `DistanceLimitType` (0=対ターゲット / 1=対オブジェクト) | 距離制限 |
| `Freezing`, `FreezeFor`, `IntervalBetweenFreezes`, `FreezeResetCombat`, `FreezeResetTerr`, `FreezeDisplayDelay` | 表示のフリーズ |
| `Subconfigurations`, `DefaultConfigurationName`, `SelectedSubconfigurationID` | サブ設定 |
| `DisableInDuty`, `DisableDisabling`, `Nodraw`, `ConditionalAnd` | その他 |

---

## 6. Priority システム

`Splatoon/Splatoon/SplatoonScripting/Priority/`

ユーザが「誰が 1 番目に塔を踏むか」等を GUI で並べ替えられる仕組み。

```csharp
public class PriorityData
{
    public string Name = "Priority list";
    public string Description = "";
    public virtual int GetNumPlayers() => 8;      // 人数を変えたいときは継承して override
    public void Draw();                            // OnSettingsDraw から呼ぶだけで UI が出る

    UniversalPartyMember? GetPlayer(Predicate<UniversalPartyMember> predicate, int position = 1, bool fromEnd = false);
    List<UniversalPartyMember>? GetPlayers(Predicate<UniversalPartyMember> predicate, bool fromEnd = false);
    int GetOwnIndex(Predicate<UniversalPartyMember> predicate, bool fromEnd = false);  // 見つからなければ -1、0 始まり
    PriorityList? GetFirstValidList();
}
```

`position` は **1 始まり**、`GetOwnIndex` は **0 始まり**。

使い方:

```csharp
public class Config
{
    public Prio1 Partner = new();
}
public class Prio1 : PriorityData
{
    public override int GetNumPlayers() => 1;
}
// OnSettingsDraw で C.Partner.Draw();
// 解決は C.Partner.GetPlayer(x => x.IGameObject != null, 1);
```

補助型:

```csharp
public class PriorityList { public List<JobbedPlayer> List; public bool IsRole; bool Test(out string? error); }
public class JobbedPlayer {
    public string Name; public HashSet<Job> Jobs; public RolePosition Role;
    bool IsPlayerEmpty(); UniversalPartyMember? ResolveByRole(RolePosition role);
    bool IsInParty(bool byRole, out UniversalPartyMember? member); string GetNameAndJob();
}
public enum RolePosition { Not_Selected = 0, T1 = 1000, T2, H1 = 2000, H2, M1 = 3000, M2, R1, R2 }
```

`Controller.RolePosition` で自分のロール位置が取れる。

---

## 7. `ScriptingEngine`

```csharp
static bool ScriptingEngine.TryDecodeLayout(string s, out Layout l)
static bool ScriptingEngine.TryDecodeElement(string s, out Element element)
```

Splatoon GUI からエクスポートした文字列を手動でデコードしたいとき用。
通常は `Controller.RegisterElementFromCode` / `RegisterLayoutFromCode` で足りる。

---

## 8. Splatoon 内部で script から使える部分

### `Splatoon.Memory.AttachedInfo` (`using Splatoon.Memory;`)

オブジェクトに紐づくキャッシュ情報。VFX / 線 / 詠唱を「発生からの経過時間つき」で拾える。

```csharp
static Dictionary<nint, CachedCastInfo>                 CastInfos;
static Dictionary<nint, List<CachedObjectEffectInfo>>   ObjectEffectInfos;
static Dictionary<nint, Dictionary<string, VFXInfo>>    VFXInfos;
static Dictionary<nint, List<CachedTetherInfo>>         TetherInfos;

static bool TryGetVfx(this IGameObject go, out Dictionary<string, VFXInfo>? fx);
static bool TryGetSpecificVfxInfo(this IGameObject go, string path, out VFXInfo info);
static List<CachedTetherInfo> GetOrCreateTetherInfo(nint ptr);
static bool TryGetCastTime(nint ptr, uint castId, out float castTime);
static bool TryGetCastTime(nint ptr, IEnumerable<uint> castId, out float castTime);
```

```csharp
public record struct VFXInfo { long SpawnTime; long Age; float AgeF; }
public readonly record struct CachedTetherInfo {
    long SpawnTime; int Param1, Param2, Param3; uint Target; long Age; float AgeF;
    bool ParamEqual(int p1, int p2, int p3);           // ほか多重定義
}
```

**用途**: 「この VFX が出てから 0.5 秒以内か」の判定に `AgeF` を使うのが定番。

### `Splatoon.Memory.Marking`

```csharp
static ulong GetMarker(uint index);
static bool  HaveMark(ICharacter obj, uint index);   // 頭上マーカー判定。公式でも最頻出
static IGameObject GetPlayer(string pronoun);        // "<1>" などのプレースホルダ解決
```

### `Splatoon.Utility.Utils` (`using Splatoon.Utility;`)

```csharp
Utils.RotatePoint(...)      // 点の回転
Utils.GetPositionXZY(...)   // 座標の XZY 変換
Utils.BrotliCompress / BrotliDecompress   // エクスポート文字列の圧縮・展開
```

### カメラ角度は Splatoon からは取れない

`Splatoon.Memory.Camera` は `internal` なのでスクリプトからは見えない
(`InternalsVisibleTo` は設定されていない)。公式スクリプトはシグネチャスキャンで
自前の `Camera` クラスを各スクリプト内に定義している
(`SplatoonScripts/Generic/ForceSetDirection.cs` が実例)。
画面基準の方向表示が要るなら `Svc.GameGui.WorldToScreen` で十分なことが多い。

詳細は [ffxiv-coordinates](../../ffxiv-coordinates/SKILL.md) スキルを参照。
