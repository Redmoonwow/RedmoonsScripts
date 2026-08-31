# RedmoonsScripts

[Splatoon](https://github.com/PunishXIV/Splatoon) 用の自作スクリプト置き場。
公式リポジトリの `SplatoonScripts/` と同じビルド環境を、独立したリポジトリとして再現したもの。

## 構成

```
RedmoonsScripts/            公式の SplatoonScripts/ 相当（ここにスクリプトを書く）
├─ RedmoonsScripts.csproj   Splatoon / ECommons / WrathCombo.API を ProjectReference
├─ update.csv               CI が自動生成（手で編集しない）
├─ Generic/
├─ Tests/
└─ Duties/{Dawntrail,Endwalker,Shadowbringers,Stormblood,Universal}/

Splatoon/                   submodule: PunishXIV/Splatoon
tools/gen_update_csv.py     update.csv 生成スクリプト
```

## セットアップ

```bash
git clone --recursive https://github.com/Redmoonwow/RedmoonsScripts.git
cd RedmoonsScripts
dotnet build RedmoonsScripts/RedmoonsScripts.csproj
```

必要なもの:

- .NET 10 SDK
- Dalamud dev libs (`%APPDATA%\XIVLauncher\addon\Hooks\dev\`) — XIVLauncher で Dalamud を一度でも起動していれば存在する

クローン済みで submodule を入れ忘れた場合:

```bash
git submodule update --init --recursive
```

## スクリプトの書き方

namespace はディレクトリ構造をそのまま `.` で連結し、空白は `_` に置換する（公式と同じ規約）。
ただしルートは公式の `SplatoonScriptsOfficial` ではなく **`RedmoonsScripts`** を使う。

```
RedmoonsScripts/Duties/Dawntrail/Dancing_Mad/P1_Arrows.cs
  → namespace RedmoonsScripts.Duties.Dawntrail.Dancing_Mad;
```

> [!IMPORTANT]
> 公式スクリプトを元にする場合も namespace は必ず `RedmoonsScripts.*` に変える。
> Splatoon はスクリプトを `{namespace}@{クラス名}` で識別しており、公式と同じ値のままだと
> 公式 `update.csv` のエントリと衝突して公式版に上書きされる。

最小の雛形:

```csharp
using Splatoon.SplatoonScripting;
using System.Collections.Generic;

namespace RedmoonsScripts.Tests;

public class SampleTest : SplatoonScript
{
    public override HashSet<uint>? ValidTerritories => null;
    public override Metadata? Metadata => new(1, "Redmoon");
}
```

`Metadata` の第1引数がバージョン。**更新を配信するたびにこの数字を上げる。**

## Splatoon 側の設定（初回のみ）

Splatoon の設定にある **Trusted repos** タブは既定で隠れている。警告文の下のチェックボックスに
チェックを入れると開く。

1. **Extra trusted sources** に追加（前方一致で判定されるので1行でよい）

   ```
   https://github.com/Redmoonwow/
   ```

2. **Extra update sources** に追加

   ```
   https://github.com/Redmoonwow/RedmoonsScripts/raw/main/RedmoonsScripts/update.csv
   ```

> [!WARNING]
> Extra update sources に登録したリストの管理者は、あなたの PC で任意のコードを実行できる。
> 自分が管理している URL 以外は絶対に登録しないこと。

これで、`Metadata` のバージョンを上げて push すると Splatoon が自動で更新を取りに来る。

## 開発ループ

1. `RedmoonsScripts/` 配下に `.cs` を書く（IntelliSense と型チェックが効く）
2. `dotnet build RedmoonsScripts/RedmoonsScripts.csproj` でコンパイルエラーを潰す
3. ゲームで試す
   - `.cs` を `%APPDATA%\XIVLauncher\pluginConfigs\Splatoon\Scripts\RedmoonsScripts\` に置く、または
   - Splatoon の Scripting タブから raw URL を指定して取得する
   - `/splatoon` → Scripting タブでリロード
4. `Metadata` のバージョンを上げて push → CI が `update.csv` を再生成 → 各環境に配信される

## update.csv

`RedmoonsScripts/**` への push で GitHub Actions が
[`tools/gen_update_csv.py`](tools/gen_update_csv.py) を実行し、`RedmoonsScripts/update.csv`
を再生成してコミットする。手で編集しても上書きされる。

ローカルで確認したい場合:

```bash
python tools/gen_update_csv.py RedmoonsScripts RedmoonsScripts/update.csv
```

## ライセンス

AGPL-3.0（Splatoon 本体と同じ）。
