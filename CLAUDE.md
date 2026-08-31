# CLAUDE.md

このファイルは、このリポジトリで作業する Claude Code への指示。

## このリポジトリは何か

[Splatoon](https://github.com/PunishXIV/Splatoon) (FFXIV の Dalamud プラグイン) 用の
自作スクリプト置き場。公式リポジトリの `SplatoonScripts/` と同じビルド環境を、独立した
リポジトリとして再現している。

`.cs` を 1 枚書くと、Splatoon が実行時に Roslyn でコンパイルしてプラグイン内にロードする。
このリポジトリの csproj は**そのスクリプトを実機に入れる前に型チェックするためだけ**に
存在する。ビルド成果物 (dll) は使わない。

## 構成

```
RedmoonsScripts/            スクリプト本体 (公式の SplatoonScripts/ 相当)
├─ RedmoonsScripts.csproj   Splatoon / ECommons / ECommons.IPC / WrathCombo.API を ProjectReference
├─ update.csv               CI が自動生成。手で編集しない
├─ Generic/                 duty 非依存のユーティリティ
├─ Tests/                   実験・解析用
└─ Duties/{Dawntrail,Endwalker,Shadowbringers,Stormblood,Universal}/

Splatoon/                   submodule: PunishXIV/Splatoon (本体 + ネストした submodule 8 個)
tools/gen_update_csv.py     update.csv 生成 (公式 ScriptUpdateFileGenerator の移植)
.github/workflows/          RedmoonsScripts/** への push で update.csv を再生成
.claude/skills/splatoon-script/   スクリプト開発用スキル (API リファレンス込み)
```

## ビルド

```bash
dotnet build RedmoonsScripts/RedmoonsScripts.csproj
```

- .NET 10 SDK が必要。`net10.0-windows7.0` / x64。
- Dalamud dev libs (`%APPDATA%\XIVLauncher\addon\Hooks\dev\`) を HintPath 参照している。
  XIVLauncher で Dalamud を一度起動していれば存在する。
- 初回は Splatoon 本体 + FFXIVClientStructs (source generator あり) をビルドするので時間がかかる。
- submodule 未取得なら `git submodule update --init --recursive`。

## スクリプトを書く・直すとき

**`splatoon-script` スキルを使う。** `.claude/skills/splatoon-script/` に
SplatoonScript の全 override、Controller / Element / Layout / Priority API、ECommons ヘルパ、
FFXIVClientStructs の構造体アクセス、公式 340 スクリプトから抽出したイディオムがまとまっている。

一次資料は `Splatoon/` submodule のソース。特に:

- `Splatoon/Splatoon/SplatoonScripting/` — スクリプト API の実装
- `Splatoon/Splatoon/Serializables/Element.cs` — element の全フィールド
- `Splatoon/ECommons/ECommons/` — ヘルパライブラリ
- `Splatoon/SplatoonScripts/` — 公式スクリプト 340 本 (生きた用例集)

## 守ること

1. **namespace は `RedmoonsScripts.*`。** ディレクトリ構造を `.` で連結し空白は `_` に置換。
   Splatoon はスクリプトを `{namespace}@{クラス名}` で識別しており、公式の
   `SplatoonScriptsOfficial.*` を使うと公式版に上書き更新される。

2. **`Metadata` のバージョンは配信のたびに上げる。**
   `public override Metadata Metadata => new(3, "Redmoon");` の形を崩さない
   (update.csv 生成器が正規表現 `override.+Metadata.+Metadata.+new\D+([0-9]+)` で拾う)。

3. **自機は `BasePlayer`。** `Player.Object` / `Svc.Objects.LocalPlayer` は使わない。
   duty replay の Base Player Override を壊す (公式では RS0030 でエラー)。

4. **`OnSetup` は element / layout 登録だけ。** 後始末が必要なものは `OnEnable` / `OnDisable`、
   状態リセットは `OnReset` に置く。

5. **`update.csv` を手で編集しない。** CI が再生成する。

6. **`Splatoon/` submodule 内のファイルを編集しない。** upstream のコード。
   公式に変更を出すなら `F:\works\80.repos\Splatoon` (Redmoonwow/Splatoon fork) 側で行う。

## API ドリフト

Splatoon submodule を更新すると override のシグネチャや Dalamud の enum 名が変わることがある。
ビルドが `CS0115` (オーバーライド先が見つからない) や `CS0117` (メンバがない) で落ちたら:

1. `Splatoon/Splatoon/SplatoonScripting/SplatoonScript.cs` の現在の宣言を確認する
2. 公式スクリプトの最新版 (`Splatoon/SplatoonScripts/`) が同じ API をどう呼んでいるか見る

過去の実例: `OnObjectEffect(uint, ushort, ushort)` → `(uint target, uint entityId, uint actionId)`、
`OnActorControl` に `p7, p8` 追加、`BattleNpcSubKind.Chocobo` → `RaceChocobo`。

## 実機での確認

1. `.cs` を `%APPDATA%\XIVLauncher\pluginConfigs\Splatoon\Scripts\RedmoonsScripts\` に置く
2. `/splatoon` → Scripting タブでリロード

配信経由で入れる場合は、Splatoon の Trusted repos タブ (既定で隠れている。警告の
チェックボックスで開く) に以下を設定しておく:

- Extra trusted sources: `https://github.com/Redmoonwow/`
- Extra update sources: `https://github.com/Redmoonwow/RedmoonsScripts/raw/main/RedmoonsScripts/update.csv`

## ライセンス

AGPL-3.0 (Splatoon 本体と同じ)。
