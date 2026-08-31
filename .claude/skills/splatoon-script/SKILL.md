---
name: splatoon-script
description: Splatoon (FFXIV Dalamud プラグイン) のスクリプトを書く・読む・直すときに使う。SplatoonScript の全 override、Controller / Element / Layout / Priority API、ECommons ヘルパ (Svc, BasePlayer, MathHelper, Hooks, ImGuiEx, EzConfig, TaskManager)、FFXIVClientStructs の構造体アクセスを網羅。新規スクリプト作成、既存スクリプトの改修、API ドリフト (シグネチャ変更) の修正、element JSON の組み立て、ギミック判定ロジックの実装に使う。
---

# Splatoon スクリプト開発

このリポジトリ (`RedmoonsScripts/`) は Splatoon のランタイムスクリプトを書く場所。
`.cs` を 1 枚書くと、Splatoon が Roslyn で実行時にコンパイルしてプラグイン内にロードする。

## 最初に読むもの

| やること | 参照 |
|---|---|
| `SplatoonScript` の override、Controller / Element / Layout / Priority | [references/splatoon-api.md](references/splatoon-api.md) |
| `Svc` / `BasePlayer` / MathHelper / Hooks / ImGuiEx / EzConfig / TaskManager | [references/ecommons-api.md](references/ecommons-api.md) |
| ゲーム構造体への生アクセス (`ActionManager.Instance()` など) | [references/clientstructs-api.md](references/clientstructs-api.md) |
| 公式 340 スクリプトから抽出した頻出パターン・レシピ | [references/idioms.md](references/idioms.md) |

実装の一次資料は `Splatoon/` submodule のソースそのもの。迷ったら
`Splatoon/Splatoon/SplatoonScripting/` と `Splatoon/ECommons/ECommons/` を直接読む。
公式スクリプト 340 本は `Splatoon/SplatoonScripts/` にあり、生きた用例集として使える。

## 骨格

```csharp
using Dalamud.Bindings.ImGui;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.ImGuiMethods;
using ECommons.Logging;
using ECommons.MathHelpers;
using Splatoon.SplatoonScripting;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RedmoonsScripts.Duties.Dawntrail.Some_Duty;

public unsafe class My_Script : SplatoonScript<My_Script.Config>
{
    public override HashSet<uint>? ValidTerritories { get; } = [1234];
    public override Metadata Metadata { get; } = new(1, "Redmoon");

    public override void OnSetup()
    {
        // element / layout の登録だけ。フック登録や状態初期化はここに書かない
        Controller.RegisterElementFromCode("Spread", """
            {"Name":"Spread","type":1,"radius":6.0,"color":3372220160,"refActorComparisonType":2}
            """);
    }

    public override void OnStartingCast(uint source, uint castId)
    {
        if (castId != 12345) return;
        if (Controller.TryGetElementByName("Spread", out var e)) e.Enabled = true;
    }

    public override void OnReset()
    {
        Controller.Hide();   // 状態のクリーンアップは必ずここ
    }

    public override void OnSettingsDraw()
    {
        ImGui.Checkbox("Enable", ref C.Enabled);
    }

    public class Config
    {
        public bool Enabled = true;
    }
}
```

## 絶対に守るルール

**1. namespace は `RedmoonsScripts.*`**
ディレクトリ構造をそのまま `.` で連結し、空白は `_` に置換する。
Splatoon はスクリプトを `{namespace}@{クラス名}` で一意識別しており、これが update.csv /
blacklist.csv の突合キーかつローカル配置先フォルダ名になる。公式の
`SplatoonScriptsOfficial.*` を流用すると公式版に上書き更新される。

**2. `Metadata` のバージョンは配信のたびに上げる**
`new(version, author, description?, website?)`。CI の update.csv 生成もこの数値を正規表現で拾う。

**3. 自機は `BasePlayer` を使う。`Player.Object` / `Svc.Objects.LocalPlayer` は使わない**
Splatoon には「Base Player Override」(duty replay 中に別プレイヤー視点で検証する機能) があり、
`BasePlayer` だけがそれを尊重する。公式リポジトリではこれが `BannedSymbols.txt` +
BannedApiAnalyzers で RS0030 エラーとして強制されている (このリポジトリでは未導入)。
どうしても生の自機が要るスクリプトは先頭に `#pragma warning disable RS0030` を書く。

**4. `OnSetup` にフックや可変状態を置かない**
`OnSetup` は「コンパイル直後に 1 度だけ」呼ばれ、対になる後始末が存在しない (設計上の意図)。
フックや購読は `OnEnable` / `OnDisable`、状態リセットは `OnReset` に置く。

**5. `ValidTerritories` の 3 つの意味を取り違えない**
- `[1234, 5678]` … その territory でのみ動作
- `[]` (空) … 全 territory で動作
- `null` … ログアウト中でも止まらず常時動作 (Generic 系ユーティリティ用)

**6. element の座標は Y と Z が入れ替わる**
`Element.refX/refY/refZ` の `refY` はゲーム座標の **Z**、`refZ` が **Y** (高さ)。
`Extensions.SetRefPosition(Vector3)` / `SetOffPosition(Vector3)` を使えば自動で入れ替えてくれる。

## 作業の進め方

1. **既存の類似スクリプトを探す** — `Splatoon/SplatoonScripts/Duties/<拡張>/` を grep する。
   同じギミック種別 (spread / stack / tower / tether / knockback) の実装が大抵ある。
2. **element は JSON 文字列で登録する** — 手で組むより、Splatoon の GUI で作って Export した
   文字列を raw string literal で貼るのが公式の標準。`Controller.RegisterElementFromCode`。
3. **判定のトリガを選ぶ** — [references/idioms.md](references/idioms.md) の「トリガ選択」表を見る。
   cast / debuff / VFX / map effect / tether / actor control のどれで拾うかで実装が決まる。
4. **ビルドで型チェックする**
   ```bash
   dotnet build RedmoonsScripts/RedmoonsScripts.csproj
   ```
   Splatoon 本体を ProjectReference しているので、API ドリフト (override シグネチャ変更、
   Dalamud enum のリネーム) はここで必ず落ちる。実機に入れる前に通す。
5. **実機で試す** — `.cs` を
   `%APPDATA%\XIVLauncher\pluginConfigs\Splatoon\Scripts\RedmoonsScripts\` に置いて
   `/splatoon` → Scripting タブでリロード。

## よくある落とし穴

- **API ドリフト**: Splatoon submodule を更新すると override のシグネチャが変わることがある
  (実例: `OnObjectEffect(uint, ushort, ushort)` → `(uint target, uint entityId, uint actionId)`、
  `OnActorControl` に `p7, p8` 追加)。`CS0115: オーバーライドする適切なメソッドが見つかりませんでした`
  が出たら `Splatoon/Splatoon/SplatoonScripting/SplatoonScript.cs` の現在の宣言を見て合わせる。
- **Dalamud enum のリネーム**: `BattleNpcSubKind.Chocobo` → `RaceChocobo` のような改名がある。
  公式スクリプトの最新版を見るのが一番速い。
- **`OnActorControl` は VOLATILE**: 毎パケット呼ばれる。重い処理を直接書かない。
- **`OnUpdate` は毎フレーム**: `EzThrottler` / `FrameThrottler` (スクリプトインスタンスごとに
  `EzThrottler` プロパティが生えている) で間引く。
- **element を消し忘れる**: `OnReset` で `Controller.Hide()` を呼ぶ。呼ばないと次の phase や
  wipe 後に前の指示が残る。
- **cross-world パーティ**: `Controller.GetPartyMembers()` は非 cross-world 限定。
  cross-world を含めるなら `UniversalParty.Members`。
