---
name: splatoon-gui
description: Splatoon スクリプトの GUI 実装。OnSettingsDraw の設定画面、ImGui (Dalamud.Bindings.ImGui) と ImGuiEx の使い分け、チェックボックス階層・enum コンボ・カラーピッカー・テーブル・Priority UI、画面中央への指示表示 (Attention Window / TimedMiddleOverlayWindow)、独自オーバーレイウィンドウ、色の扱い (EColor / ImGuiColors / GradientColor / element の uint 形式)、アイコンと画像、ID 衝突や描画コールバックの副作用などの落とし穴。設定 UI を足す・オーバーレイを出す・見た目を直すときに使う。
---

# Splatoon スクリプトの GUI

スクリプトが絵を出す経路は 4 つある。**まずどれを使うべきかを決める。**

| 経路 | 何が出るか | いつ使う |
|---|---|---|
| **Element** | 3D 空間の図形・追従テキスト | ギミックの位置指示。**基本はこれ**。詳細は `splatoon-script` スキル |
| **`OnSettingsDraw()`** | Splatoon 設定内のスクリプト設定欄 | ユーザに選ばせる項目 |
| **Attention Window** | 画面中央の 1 行指示 (Splatoon が管理) | 「頭割り」「北へ」等の短い文字指示 |
| **独自ウィンドウ** | 任意の ImGui ウィンドウ | 上記で足りない表示・デバッグ用 |

素の drawlist (`ImGui.GetForegroundDrawList()`) に自前で描くのは最後の手段。
element ならレンダエンジン選択・射影補正・ユーザのテーマ設定が全部効く。

---

## 1. ImGui の土台

```csharp
using Dalamud.Bindings.ImGui;    // 新しい Dalamud バインディング。181 スクリプトが import
using ECommons.ImGuiMethods;     // ImGuiEx, EColor, Notify など。210 スクリプト
using Dalamud.Interface.Colors;  // ImGuiColors
```

公式スクリプトでの生 ImGui 呼び出し頻度 (上位):

```
ImGui.Text 936 / TextUnformatted 569 / Checkbox 357 / SetNextItemWidth 304 / SameLine 291
Separator 266 / TableNextColumn 253 / TableSetupColumn 167 / Indent 154 / Unindent 151
ColorEdit4 120 / CollapsingHeader 119 / Button 116 / Spacing 111 / PushID 87 / PopID 87
```

**`ImGui.Text` と `ImGuiEx.Text` の使い分け**

- 色をつける・フォントを変える → `ImGuiEx.Text(EColor.Red, "...")`
- ユーザ入力やゲームから来た文字列をそのまま出す → `ImGui.TextUnformatted(s)`
  (書式指定子の解釈を避ける)
- それ以外はどちらでもよい。`ImGuiEx.Text` の方が短い

**ID 衝突**

同じラベルのウィジェットを複数出すと状態が共有されて壊れる。ループで並べるときは必ず囲む:

```csharp
for (var i = 0; i < list.Count; i++)
{
    ImGui.PushID(i);
    ImGui.Checkbox("Enabled", ref list[i].Enabled);
    ImGui.PopID();
}
```

`##` サフィックス (`"表示名##unique-id"`) でも同じことができる。

**設定値は `ref` で直接渡す**

```csharp
ImGui.Checkbox("Show all", ref C.ShowAll);      // C は設定オブジェクト。書き換えは自動保存
```

`C` のフィールドは `readonly` にしない。プロパティにすると `ref` が取れないのでフィールドにする。

---

## 2. `OnSettingsDraw()` — 設定画面

`OnSettingsDraw` を override すると、Splatoon の Scripting タブにそのスクリプトの設定欄が生える。
**override しなければ設定欄自体が出ない** (`SplatoonScript.DoSettingsDraw` が判定している)。

```csharp
public unsafe class My_Script : SplatoonScript<My_Script.Config>
{
    public override void OnSettingsDraw()
    {
        ImGui.Checkbox("全員に表示する", ref C.ShowAll);
        if (!C.ShowAll)
        {
            ImGui.Indent();
            ImGui.Checkbox("相方だけ表示", ref C.ShowPartner);
            if (C.ShowPartner) C.Partner.Draw();     // Priority UI がそのまま出る
            ImGui.Unindent();
        }

        ImGui.Separator();

        ImGuiEx.EnumCombo("戦法", ref C.Strategy);
        ImGuiEx.HelpMarker("説明をここに書く");

        ImGuiEx.RadioButtonBool("左", "右", ref C.IsLeft, sameLine: true);

        ImGui.SetNextItemWidth(200f);
        ImGui.ColorEdit4("色", ref C.Color, ImGuiColorEditFlags.NoInputs);
    }

    public class Config
    {
        public bool ShowAll = true;
        public bool ShowPartner = false;
        public Strategy Strategy = Strategy.Standard;
        public bool IsLeft = true;
        public Vector4 Color = 0xFF00FF00u.ToVector4();
        public Prio Partner = new();
    }
    public enum Strategy { Standard, Alternative }
    public class Prio : PriorityData { public override int GetNumPlayers() => 1; }
}
```

### 設定 UI の定番部品

```csharp
// 階層 (チェックが入ったときだけ子項目を出す)
ImGui.Checkbox("親", ref C.Parent);
if (C.Parent) { ImGui.Indent(); /* 子 */ ImGui.Unindent(); }

// enum のコンボ (186 回)
ImGuiEx.EnumCombo("ラベル", ref C.SomeEnum);
ImGuiEx.EnumCombo("ラベル", ref C.SomeEnum, x => x != Some.Hidden);              // filter
ImGuiEx.EnumCombo("ラベル", ref C.SomeEnum, names: new Dictionary<Some,string>{...});
ImGuiEx.EnumCombo(200f, "ラベル", ref C.SomeEnum);                                // 幅指定
ImGuiEx.EnumCombo("ラベル", ref C.NullableEnum, nullName: "未選択");              // T? 版
```

enum の表示名は `[EnumMemberName("表示名")]` 属性で差し替えられる
(`ECommons.ImGuiMethods.EnumMemberNameAttribute`)。

```csharp
// 二択 (48 回)。sameLine で横並び
ImGuiEx.RadioButtonBool("タンク", "ヒーラー", ref C.IsTank, sameLine: true);
ImGuiEx.RadioButtonBool("見出し", "真", "真の説明", "偽", "偽の説明", ref C.Flag);

// コレクションへの出し入れをチェックボックスで (33 回)
ImGuiEx.CollectionCheckbox("1", 1u, C.EnabledSlots);
ImGuiEx.CollectionCheckbox("全部", allValues, C.EnabledSlots);

// 説明マーカー (82 回)
ImGuiEx.HelpMarker("補足");

// 折りたたみ (70 回)
if (ImGuiEx.CollapsingHeader("詳細設定")) { ... }
ImGuiEx.TreeNodeCollapsingHeader("詳細設定", () => { ... });   // 中身を Action で渡す版

// テーブル (EzTableEntry 185 回)
ImGuiEx.EzTable([
    new ImGuiEx.EzTableEntry("名前",  () => ImGui.TextUnformatted(row.Name)),
    new ImGuiEx.EzTableEntry("ロール", stretch: true, () => ImGui.TextUnformatted(row.Role)),
]);

// 数値
ImGui.SetNextItemWidth(150f);
ImGui.DragFloat("半径", ref C.Radius, 0.1f, 0f, 30f);
ImGuiEx.SliderFloat("透明度", ref C.Alpha, 0f, 1f);
ImGuiEx.SliderIntAsFloat("秒", ref C.DelayMs, 0, 5000, divider: 1000);

// 文字列
ImGuiEx.InputTextMultilineExpanding("メモ", ref C.Note, 500, 2, 10);

// 誤爆させたくないボタン
if (ImGuiEx.ButtonCtrl("リセット")) { ResetEverything(); }
```

### レイアウトの作法

- 幅は `ImGui.SetNextItemWidth(x)` か `ImGuiEx.SetNextItemWidthScaled(x)` で明示する。
  無指定だと設定欄の幅に引きずられて崩れる。
- 区切りは `ImGui.Separator()` / `ImGui.Spacing()`。
- 設定欄は狭い。**セクションが 3 つ以上になるなら `ImGuiEx.CollapsingHeader` で畳む。**
- 基底クラスのコメントどおり「Keep it simple」。凝った UI はスクリプトの寿命を縮める。

---

## 3. Attention Window — 画面中央の指示

Splatoon が用意している共通の指示表示。**ユーザがスクリプト単位で無効化できる**
(`Config.DisabledAttentionWindowScripts`) ので、これに依存しきらない。

```csharp
public override void OnUpdate()
{
    if (!_shouldWarn) return;

    Controller.DisplayAttentionWindowLine("頭割り");
    Controller.DisplayAttentionWindowLine(EColor.RedBright, "$1 と組む", _partnerName);
    Controller.DisplayAttentionWindowLine(() => {
        ImGuiEx.Text(EColor.YellowBright, "北へ");
    });
}
```

守ること:

1. **毎フレーム呼び続けないと閉じる。** 出したままにしたいなら `OnUpdate` から呼ぶ。
2. **渡した `Action` は同じフレームでは実行されない。** キューに積まれて後で描かれる。
   表示したい値は呼ぶ前に確定させ、クロージャに入れて渡す。
3. **`Action` は複数回呼ばれ得る。** 中で状態を書き換えない (カウンタを回す、element を
   いじる、などをしない)。
4. ウィンドウのタイトルはスクリプト名から自動生成される (`_` は空白に置換)。

---

## 4. 独自オーバーレイウィンドウ

element でも Attention Window でも足りないときだけ。ECommons が土台を用意している。

### `TimedMiddleOverlayWindow` — 一定時間だけ中央に出す (公式で 21 回)

一番よく使われるパターン。**作って捨てるだけ**で、自動で消える。

```csharp
using ECommons.ImGuiMethods;
using Dalamud.Interface.Colors;

_ = new TimedMiddleOverlayWindow("Dark", 4000, () =>
{
    ImGui.SetWindowFontScale(2f);
    ImGuiEx.Text(ImGuiColors.DalamudRed, "離れる");
}, 400);
```

`new TimedMiddleOverlayWindow(string name, long destroyAfterMS, Action draw, int? topOffset = null, Vector4? bgCol = null)`

- `name` はウィンドウ ID。同じ名前を同時に 2 つ作らない。
- `topOffset` は画面上端からのオフセット (px)。省略時は中央寄り。
- `ImGui.SetWindowFontScale(2f)` で大きくするのが定番。
- 入力を奪わない (`NoInputs`) ので戦闘の邪魔にならない。

`MiddleOverlayWindow` は時間で消えない版。自分で `Dispose()` する。

### `EzOverlayWindow` — 画面端に固定

```csharp
public class MyOverlay : EzOverlayWindow
{
    public MyOverlay() : base("MyOverlay",
        HorizontalPosition.Right, VerticalPosition.Top, offset: new Vector2(-10, 10)) { }

    public override void DrawAction()
    {
        ImGuiEx.Text("残り 3 回");
    }
}
```

`HorizontalPosition` = `Left` / `Middle` / `Right`、`VerticalPosition` = `Top` / `Middle` / `Bottom`。
`EzFullscreenOverlayWindow` は全画面版 (自前 drawlist を敷きたいとき)。

いずれも `Dalamud.Interface.Windowing.Window` 派生なので `WindowSystem` に登録して使う。
**登録は `OnEnable`、解除と `Dispose` は `OnDisable` で行う** (`OnSetup` は後始末が無い)。

### トースト通知

```csharp
Notify.Info("読み込み完了");
Notify.Success("...");  Notify.Warning("...");  Notify.Error("...");  Notify.Plain("...");
```

---

## 5. 色

### 形式が 3 つある

| 形式 | 使う場所 | 例 |
|---|---|---|
| `uint` **`0xAABBGGRR`** | `Element.color`, `overlayTextColor`, `overlayBGColor` | `0xC80000FF` = 不透明度 200 の赤 |
| `Vector4` (RGBA 0〜1) | ImGui のほぼ全部 | `new(1, 0, 0, 1)` |
| `EzColor` | 変換のハブ | `EzColor.From(0xC80000FF)` |

**element の `uint` はバイト順が A→B→G→R。** RGB 感覚で `0xFF0000` と書くと青になる。

変換:

```csharp
using ECommons;                  // ConversionHelpers
using ECommons.ImGuiMethods;

uint    u = someVector4.ToUint();          // ImGui.ColorConvertFloat4ToU32
Vector4 v = someUint.ToVector4();          // 185 回。最頻出の変換
Vector4 v2 = 0xFF0000u.Vector4FromRGB();   // RGB 24bit から (alpha 指定可)
Vector4 v3 = 0xFF0000FFu.Vector4FromRGBA();

var ez = EzColor.From(0xC80000FF);
ez.Vector4;  ez.U32;  ez.ARGB;  ez.RGBA;
```

### 定数

```csharp
using ECommons.ImGuiMethods;     // EColor: Red RedBright RedDark Green GreenBright Blue BlueSky
                                 //         White Black Yellow YellowBright Orange Cya CyanBright
                                 //         Violet Purple ...  (Vector4)
using Dalamud.Interface.Colors;  // ImGuiColors: DalamudRed DalamudYellow DalamudOrange
                                 //              DalamudWhite DalamudGrey HealerGreen TankBlue ...
using Splatoon.Utility;          // Colors: LightYellow Red DarkRed Orange Gray Green Yellow (uint)
                                 // Colors.MultiplyAlpha(uint, float), Colors.Lerp(uint, uint, float)
```

### 点滅させる — `GradientColor` (公式で 114 回)

2 色を往復させる。危険表示や「今これ」の強調に使う定番。

```csharp
using ECommons.ImGuiMethods;

// Vector4 を返す。周期はミリ秒 (既定 1000)
var col = GradientColor.Get(C.ColorA, C.ColorB, 333);

// element に入れるなら uint に変換
if (Controller.TryGetElementByName("Bait", out var e))
    e.color = GradientColor.Get(C.ColorA, C.ColorB).ToUint();

// uint しか持っていないとき
e.color = GradientColor.Get(C.A.ToVector4(), C.B.ToVector4()).ToUint();
```

`Controller.AttentionColor` を使うと、ユーザが設定した注意色 (レインボー/グラデ/固定) に
自動追従する。自前のグラデより馴染むので、単純な「危険」表示ならこちらを優先する。

---

## 6. アイコン・画像

```csharp
using Dalamud.Interface;                  // FontAwesomeIcon
using Dalamud.Interface.Components;       // ImGuiComponents (公式スクリプトで 23 回)
using ECommons.ImGuiMethods;

ImGuiEx.Icon(FontAwesomeIcon.ExclamationTriangle);
ImGuiEx.IconWithText(FontAwesomeIcon.Info, "説明");
ImGuiEx.IconWithTooltip(FontAwesomeIcon.Question, "ツールチップ");
if (ImGuiEx.IconButton(FontAwesomeIcon.Trash, "delete")) { ... }
if (ImGuiEx.IconButtonWithText(FontAwesomeIcon.Plus, "追加")) { ... }

// ゲーム内アイコン / URL 画像 (非同期ロード。取れるまで false)
if (ThreadLoadImageHandler.TryGetIconTextureWrap(iconId, hq: false, out var tex))
    ImGui.Image(tex.Handle, new Vector2(32, 32));
```

ジョブアイコンは `job.GetIcon()` (`ECommons.ExcelServices`) で ID が取れる。

---

## 7. 描画中の状態管理

ImGui は毎フレーム全部描き直す。**描画関数に「今回だけ覚えておきたい値」を置く場所がない**ので、
ECommons が 2 つ用意している。

```csharp
using ECommons.ImGuiMethods;

// 呼び出し位置をキーに ref を返す。ローカルな UI 状態 (開閉、入力途中の文字列) 向け
ref var filter = ref Ref<string>.Get("");
ImGui.InputText("検索", ref filter, 100);

// 参照で持ち回したい値の箱
var box = new Box<int>(0);
```

永続化したい値は `Config` に置く。`Ref<T>` はセッション内だけ。

---

## 8. 落とし穴

- **描画コールバックで状態を変えない。** Attention Window の `Action` も
  `TimedMiddleOverlayWindow` の `draw` も複数回・遅れて呼ばれる。element の enable/disable や
  カウンタ更新は `OnUpdate` 等のロジック側でやる。
- **`ImGui.SetWindowFontScale` は元に戻さない。** ウィンドウ単位なので、独自ウィンドウ内なら
  問題ないが、設定欄で使うと以降の描画に影響する。設定欄では使わない。
- **`Begin`/`End`、`PushID`/`PopID`、`BeginTable`/`EndTable` の対応を崩さない。**
  例外で抜けると ImGui スタックが壊れてゲーム全体の UI が崩壊する。
  リスクのある処理は `try`/`finally` で囲むか、`ImGuiEx.EzTable` のような
  対応済みヘルパを使う。
- **`ref` が取れないものを設定に置かない。** プロパティ・`readonly`・
  コレクションの要素 (`list[i].Field` は構造体だと不可) は `ImGui.Checkbox` に渡せない。
  設定クラスは可変フィールドの POCO にする。
- **設定欄は狭い。** 幅を明示せずに長いラベルを並べると折り返して読めなくなる。
- **多言語**: 表示文字列は `Loc(en: "Stack", jp: "頭割り")` で切り替えられる
  (`SplatoonScript.Loc`)。自分専用なら日本語直書きでよい。
- **重い処理を描画に混ぜない。** `Svc.Objects` の全走査を `OnSettingsDraw` でやると
  設定を開いている間ずっと毎フレーム走る。

---

## 9. 参考になる公式スクリプト

| 目的 | ファイル (`Splatoon/SplatoonScripts/`) |
|---|---|
| 設定 UI + Priority + 階層チェックボックス | `Duties/Dawntrail/Dancing Mad/P2_Forsaken.cs` |
| `TimedMiddleOverlayWindow` で中央に指示 | `Duties/Dawntrail/The Futures Rewritten/FullToolerPartyOnlyScrtipts/P5 Paradise Regained Full Toolers.cs` |
| `GradientColor` で点滅させる | `Duties/Dawntrail/Dancing Mad/P5_Exaflare_beta.cs`, `P3_Earthquake.cs` |
| 大きめの設定画面 | `Generic/` 以下の各種ユーティリティ |

ImGuiEx の全メソッドは `splatoon-script` スキルの
[references/ecommons-api.md](../splatoon-script/references/ecommons-api.md) にまとめてある。
実装は `Splatoon/ECommons/ECommons/ImGuiMethods/ImGuiEx/`。
