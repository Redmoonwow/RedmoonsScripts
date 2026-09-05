using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Colors;
using ECommons;
using ECommons.Automation;
using ECommons.ChatMethods;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.GameFunctions;
using ECommons.GameFunctions.VirtualTableClassifier;
using ECommons.GameHelpers;
using ECommons.Hooks.ActionEffectTypes;
using ECommons.ImGuiMethods;
using ECommons.Logging;
using ECommons.MathHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Splatoon;
using Splatoon.Memory;
using Splatoon.SplatoonScripting;
using Splatoon.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Status = Lumina.Excel.Sheets.Status;
using UIColor = ECommons.ChatMethods.UIColor;

namespace RedmoonsScripts.Duties.Dawntrail.Dancing_Mad;

/// <summary>
/// Dancing Mad (Ultimate) P4 デバフのリマインダ。
/// </summary>
/// <remarks>
/// Derived from <c>SplatoonScripts/Duties/Dawntrail/Dancing Mad/P4_Debuff_Reminder.cs</c>
/// in PunishXIV/Splatoon (v15, NightmareXIV / mirage).
/// Licensed under AGPL-3.0, same as the upstream repository.
///
/// このフェーズのケフカは「正直な分身」と「嘘つきな分身」を出す。嘘つきが撒いたデバフは
/// 名前と意味が裏返る (頭割り -> 散開、動くな -> 動け、範囲 -> ドーナツ)。
/// デバフのアイコンを見ても分からないので、このスクリプトが代わりに見分けて、
/// 裏返したあとの言葉で出す。仕組みは 3 段:
///   1. OnVFXSpawn  分身が湧いたときのエフェクトで「その分身が正直か」を覚える (IsTruth)
///   2. OnActionEffectEvent  技を撃った分身を照合して「今の回が嘘か」を確定する (IsLie)
///   3. OnGainBuffEffect  嘘の回に付いたデバフを個別に記録する (_fakeStatuses)
/// 以降の表示はすべて 3 の記録を見る。ここが空なら全部「本物」として素直に出す。
///
/// 上流からの変更:
///   v16 名前空間を RedmoonsScripts に変える (公式 update.csv と衝突させないため)
///   v16 ドーナツ/範囲の予告秒数スライダが視線の秒数を書き換えていたのを直す
///   v16 "Print final positions" のチェックを外しても出ていたのを直す
///   v16 100 行超の 3 関数を分割し、1 か所でしか使わない定数を直書きにする
/// </remarks>
public class P4_Debuff_Reminder : SplatoonScript<P4_Debuff_Reminder.Config>
{
    #region static tables
    /********************************************************************/
    /* static tables                                                    */
    /********************************************************************/
    // デバフ ID を意味ごとにまとめたもの。名前は「本物のときの意味」で、
    // 嘘の回に付いたときは行末のコメントの意味に裏返る。
    // 同じ意味に ID が複数あるのは、過去のコンテンツで使われた ID を使い回しているため。

    private static class Debuff
    {
        public static readonly uint[] DontMove   = [5546, 1072, 1384, 2657, 3793, 3802, 4144];  // 嘘 -> 動け
        public static readonly uint[] LookAway   = [5543, 452];                                 // 嘘 -> 見ろ
        public static readonly uint[] Stack      = [1023, 5545, 2142];                          // 嘘 -> 散開
        public static readonly uint[] Spread     = [587, 3799, 5544];                           // 嘘 -> 頭割り
        public static readonly uint[] FireSpread = [1600, 5547];                                // 嘘 -> ドーナツ
        public static readonly uint[] Donut      = [1601, 5548];                                // 嘘 -> 範囲
        public static readonly uint   Live       = 454;                                         // ギミックを取れ
        public static readonly uint[] Die        = [1382, 5464];                                // ギミックを外せ
        public static readonly uint[] WhiteWound = [4887, 5541];                                // Live なら黒 / Die なら白
        public static readonly uint[] BlackWound = [4888, 5542];                                // Live なら白 / Die なら黒

        // OnGainBuffEffect が「見るべきデバフか」を判定するための全 ID。
        // 上流はここを実行時のリフレクションで組んでいたが、フィールドを 1 つ足したときに
        // 黙って拾われる方が危ないので、明示的に並べる。足したらここにも足すこと。
        public static readonly uint[] All =
            [.. DontMove, .. LookAway, .. Stack, .. Spread, .. FireSpread, .. Donut,
             Live, .. Die, .. WhiteWound, .. BlackWound];
    }

    #endregion

    #region private fields
    /********************************************************************/
    /* private fields                                                   */
    /********************************************************************/
    // 実行時に変化する状態。すべてこの区画にあり、他所では宣言されない。
    // OnReset が戻すもの / 戻さないものが混ざっているので、足したら OnReset も見ること。

    // ---- OnReset が戻す ------------------------------------------------------
    private readonly Dictionary<uint, bool> _isTruth = [];   // 分身の EntityId -> 正直な分身か
    private List<StatusInfo> _fakeStatuses = [];             // 嘘の回に付いたデバフ。Import で差し替わる
    private readonly Dictionary<(string Text, UIColor Color), List<string>> _otherInfoQueue = [];
    private bool? _isFakeBlowout;                            // 終盤の扇が嘘か。null = 未確定
    private bool? _isFakeLightning;                          // 終盤の落雷が嘘か。null = 未確定
    private bool _falseAnnounced;                            // 終盤の立ち位置を出したか。一発ガード

    // ---- OnReset が戻さない: 次の回の頭で必ず上書きされる --------------------
    private bool _isLie;                                     // 今の回を撃ったのが嘘つきの分身か

    #endregion

    #region properties
    /********************************************************************/
    /* properties                                                       */
    /********************************************************************/
    public override HashSet<uint>? ValidTerritories { get; } = [1363];   // Dancing Mad (Ultimate)
    public override Metadata Metadata { get; } = new(16, "NightmareXIV, mirage, Redmoon");

    /// <summary>ケフカ本体が居て殴れる = このフェーズの最中。</summary>
    /// <remarks>フェーズ外でデバフを拾うと、別ギミックのスタックを誤って記録してしまう。
    /// 表示と記録の両方をこれ 1 つで塞いでいる。</remarks>
    private bool PhaseActive => Svc.Objects.Any(x => x.DataId == 18475 && x.IsTargetable);

    #endregion

    #region public methods (SplatoonScript overrides)
    /********************************************************************/
    /* public methods (SplatoonScript overrides)                        */
    /********************************************************************/
    // Splatoon から呼ばれる入口。役割分担は次のとおり:
    //   OnVFXSpawn           分身が正直か嘘つきかを覚える
    //   OnActionEffectEvent  技を撃った分身から「今の回が嘘か」を確定する
    //   OnGainBuffEffect     デバフ 1 個ごとに、嘘なら記録してチャットに出す
    //   OnUpdate             覚えた内容をもとに、毎フレーム表示を組み直す
    // 判定の実体はすべて下の private 区画にある。ここは分岐を持たない。

    public override void OnSetup()
    {
        Controller.RegisterElementFromCode("Black", """
            {"Name":"","type":3,"refY":40.0,"radius":10.5,"fillIntensity":0.6,"refActorNPCNameID":6055,"refActorRequireCast":true,"refActorCastId":[50069],"refActorComparisonType":6,"includeRotation":true}
            """);
        Controller.RegisterElementFromCode("White", """
            {"Name":"","type":3,"refY":40.0,"radius":10.5,"fillIntensity":0.6,"refActorNPCNameID":6055,"refActorRequireCast":true,"refActorCastId":[50068],"refActorComparisonType":6,"includeRotation":true}
            """);
        Controller.RegisterElementsFromMultilineCode("""
            {"Name":"LookAway","type":1,"radius":0.0,"fillIntensity":0.5,"overlayBGColor":2550136832,"overlayTextColor":4278190335,"thicc":3.0,"overlayText":"LOOK AWAY","refActorName":"*","refActorRequireBuff":true,"refActorBuffId":[5543],"refActorUseBuffTime":true,"refActorBuffTimeMax":15.0,"tether":true,"overlayVOffset":2.0}
            {"Name":"LookAt","type":1,"radius":0.0,"color":3355508521,"fillIntensity":0.5,"overlayBGColor":2550136832,"overlayTextColor":4278255376,"thicc":3.0,"overlayText":"LOOK AT","refActorName":"*","refActorRequireBuff":true,"refActorBuffId":[5543],"refActorUseBuffTime":true,"refActorBuffTimeMax":15.0,"tether":true,"overlayVOffset":2.0}
            {"Name":"EyeScope","type":4,"radius":15.0,"coneAngleMin":-45,"coneAngleMax":45,"color":3355506687,"fillIntensity":0.125,"thicc":3.0,"refActorType":1,"includeRotation":true,"FillStep":99.0,"RenderEngineKind":2}
            {"Name":"Hint","type":1,"radius":0.0,"Filled":false,"fillIntensity":0.5,"overlayTextColor":4292739327,"overlayVOffset":5.0,"thicc":0.0,"overlayText":"test","refActorType":1}
            {"Name":"StackSupport","refX":100.0,"refY":89.0,"radius":3.0,"Donut":0.5,"color":3355508521,"fillIntensity":0.5,"overlayBGColor":2650800128,"overlayTextColor":4280024832,"overlayVOffset":1.2,"thicc":4.0,"overlayText":"Stack support","tether":true}
            {"Name":"StackDPS","refX":100.0,"refY":111.0,"radius":3.0,"Donut":0.5,"color":3355508521,"fillIntensity":0.5,"overlayBGColor":2650800128,"overlayTextColor":4280024832,"overlayVOffset":1.2,"thicc":4.0,"overlayText":"stack dps","tether":true}
            {"Name":"SpreadSupport","refX":89.0,"refY":100.0,"radius":3.0,"Donut":0.5,"color":3355501823,"fillIntensity":0.5,"overlayBGColor":2650800128,"overlayTextColor":4278255605,"overlayVOffset":1.2,"thicc":4.0,"overlayText":"Spread support","tether":true}
            {"Name":"SpreadDPS","refX":111.0,"refY":100.0,"radius":3.0,"Donut":0.5,"color":3355501823,"fillIntensity":0.5,"overlayBGColor":2650800128,"overlayTextColor":4278255605,"overlayVOffset":1.2,"thicc":4.0,"overlayText":"Spread dps","tether":true}
            {"Name":"StackSupport_2","refX":100.0,"refY":89.0,"radius":3.0,"Donut":0.5,"color":3355508521,"fillIntensity":0.5,"overlayBGColor":2650800128,"overlayTextColor":4280024832,"overlayVOffset":1.2,"thicc":4.0,"overlayText":"Stack support","tether":true}
            {"Name":"StackDPS_2","refX":100.0,"refY":111.0,"radius":3.0,"Donut":0.5,"color":3355508521,"fillIntensity":0.5,"overlayBGColor":2650800128,"overlayTextColor":4280024832,"overlayVOffset":1.2,"thicc":4.0,"overlayText":"stack dps","tether":true}
            {"Name":"SpreadSupport_2","refX":89.0,"refY":100.0,"radius":3.0,"Donut":0.5,"color":3355501823,"fillIntensity":0.5,"overlayBGColor":2650800128,"overlayTextColor":4278255605,"overlayVOffset":1.2,"thicc":4.0,"overlayText":"Spread support","tether":true}
            {"Name":"SpreadDPS_2","refX":111.0,"refY":100.0,"radius":3.0,"Donut":0.5,"color":3355501823,"fillIntensity":0.5,"overlayBGColor":2650800128,"overlayTextColor":4278255605,"overlayVOffset":1.2,"thicc":4.0,"overlayText":"Spread dps","tether":true}
            {"Name":"MiddleGaze","refX":99.61274,"refY":99.88139,"refZ":-1.9073486E-06,"radius":4.0,"Donut":0.5,"fillIntensity":0.5,"overlayVOffset":1.2,"thicc":6.0,"tether":true}

            {"Name":"MiddleDrop","refX":99.61274,"refY":99.88139,"refZ":-1.9073486E-06,"radius":3.0,"Donut":0.5,"fillIntensity":0.5,"overlayTextColor":4278228223,"overlayVOffset":1.2,"thicc":6.0,"overlayText":"$ELEMENT","tether":true}
            
            """);
        Controller.RegisterLayoutFromCode("""
            ~Lv2~{"Enabled":false,"Name":"Language","ZoneLockH":[1363],"ElementsL":[{"Name":"LookAt","overlayText":"Look at in #","overlayTextIntl":{"Jp":"#秒後に視線"}},{"Name":"LookAway","overlayText":"Look AWAY in #","overlayTextIntl":{"Jp":"#秒後に視線外す"}},{"Name":"Spread","overlayText":"Spread in #","overlayTextIntl":{"Jp":"#秒後に散開"}},{"Name":"Stack","overlayText":"Stack in #","overlayTextIntl":{"Jp":"#秒後に頭割り"}},{"Name":"DontMove","overlayText":"Don't move in #","overlayTextIntl":{"Jp":"#秒後に動くな"}},{"Name":"Move","overlayText":"Move in #","overlayTextIntl":{"Jp":"#秒後に動け"}},{"Name":"DropDonut","overlayText":"Drop donut in #","overlayTextIntl":{"Jp":"#秒後にドーナツ"}},{"Name":"DropAOE","overlayText":"Drop AOE in #","overlayTextIntl":{"Jp":"#秒後に範囲"}}]}
            """);
        Controller.RegisterLayoutFromCode("""
            ~Lv2~{"Enabled":false,"Name":"Move","ZoneLockH":[1363],"ElementsL":[{"Name":"","type":3,"refX":-0.5,"refY":-0.5,"offX":-2.0,"offY":-2.0,"radius":0.0,"color":3357671168,"fillIntensity":0.345,"thicc":4.0,"refActorType":1,"LineEndB":1},{"Name":"","type":3,"refX":-0.5,"refY":0.5,"offX":-2.0,"offY":2.0,"radius":0.0,"color":3357671168,"fillIntensity":0.345,"thicc":4.0,"refActorType":1,"LineEndB":1},{"Name":"","type":3,"refX":0.5,"refY":0.5,"offX":2.0,"offY":2.0,"radius":0.0,"color":3357671168,"fillIntensity":0.345,"thicc":4.0,"refActorType":1,"LineEndB":1},{"Name":"","type":3,"refX":0.5,"refY":-0.5,"offX":2.0,"offY":-2.0,"radius":0.0,"color":3357671168,"fillIntensity":0.345,"thicc":4.0,"refActorType":1,"LineEndB":1}]}
            """);
        Controller.RegisterLayoutFromCode("""
            ~Lv2~{"Enabled":false,"Name":"DontMove","ZoneLockH":[1363],"ElementsL":[{"Name":"","type":3,"refX":-0.5,"refY":-0.5,"offX":-2.0,"offY":-2.0,"radius":0.0,"fillIntensity":0.345,"thicc":4.0,"refActorType":1,"LineEndA":1},{"Name":"","type":3,"refX":0.5,"refY":0.5,"offX":2.0,"offY":2.0,"radius":0.0,"fillIntensity":0.345,"thicc":4.0,"refActorType":1,"LineEndA":1},{"Name":"","type":3,"refX":0.5,"refY":-0.5,"offX":2.0,"offY":-2.0,"radius":0.0,"fillIntensity":0.345,"thicc":4.0,"refActorType":1,"LineEndA":1},{"Name":"","type":3,"refX":-0.5,"refY":0.5,"offX":-2.0,"offY":2.0,"radius":0.0,"fillIntensity":0.345,"thicc":5.0,"refActorType":1,"LineEndA":1}]}
            """);
    }

    public override void OnReset()
    {
        _isTruth.Clear();
        _fakeStatuses.Clear();
        _otherInfoQueue.Clear();
        _isFakeBlowout = null;
        _isFakeLightning = null;
        _falseAnnounced = false;
    }

    public override void OnUpdate()
    {
        Controller.Hide();
        if(!PhaseActive) return;

        FlushOtherCallouts();
        ShowWoundColor();
        ShowGaze();
        ShowStackOrSpread();
        ShowDropShape();

        // 動くな/動けだけは文字で出す。要素は 1 つしかないので、出す文面もここで決める。
        if(Controller.TryGetElementByName("Hint", out var hint))
        {
            hint.Enabled = true;
            hint.overlayText = ShowMoveOrDontMove();
        }

        ResolveFinalDebuff();
    }

    public override void OnVFXSpawn(uint target, string vfxPath)
    {
        // 19510 / 19507 = 正直と嘘つきの分身。片方だけが正しい技を撃つ。
        // どちらの ID がどちらとは決まっておらず、湧いたときのエフェクトでしか区別できない。
        if(target.GetObject()?.DataId.EqualsAny<uint>(19510, 19507) != true) return;

        if(vfxPath is "vfx/common/eff/z3oy_stlp7_c0c.avfx" or "vfx/common/eff/z3oy_stlp5_c0c.avfx")
            _isTruth[target] = true;
        else if(vfxPath is "vfx/common/eff/z3oy_stlp6_c0c.avfx" or "vfx/common/eff/z3oy_stlp4_c0c.avfx")
            _isTruth[target] = false;
    }

    public override void OnActionEffectEvent(ActionEffectSet set)
    {
        // 技を撃ったのがどちらの分身かで、この回のデバフが嘘かどうかが決まる。
        // デバフが付くのはこの直後なので、ここで先に確定しておく必要がある。
        if(set.Action == null) return;
        if(set.Source?.ObjectId.EqualsAny(_isTruth.Keys) != true) return;

        _isLie = !_isTruth[set.Source.ObjectId];
    }

    public override void OnGainBuffEffect(uint sourceId, FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        if(!PhaseActive) return;
        if(!Debuff.All.Contains(status.StatusId)) return;
        if(!sourceId.TryGetPlayer(out var player)) return;

        // 嘘の回に付いたものは 1 個ずつ覚える。以降の表示はすべてこの記録を見る。
        if(_isLie) _fakeStatuses.Add(new(sourceId, status.StatusId));

        var isSelf = player.AddressEquals(BasePlayer);
        // 読み上げ用に他人ぶんも出す設定のときは、自分ぶんの文面は出さない。
        // 同じ内容が「自分向け」と「$ 付き」で二重に流れるため。
        if(isSelf && !C.ShowOthers) AnnounceSelf(status);
        if(isSelf) AnnounceSelfShape(status);
        if(C.ShowOthers) EnqueueOtherCallout($"{player.Name}", status);
    }

    public override unsafe void OnStartingCast(uint sourceId, PacketActorCast* packet)
    {
        // 49884 = このフェーズの開始技。前の周回の表示が残っていると邪魔なので、ここで捨てる。
        if(packet->ActionType == (byte)ActionType.Action && packet->ActionID == 49884)
            Controller.Reset();
    }

    public override void OnSettingsDraw()
    {
        DrawBehaviourSettings();
        ImGui.Separator();
        DrawMessageSettings();
        DrawDebug();
    }

    #endregion

    #region private methods : 毎フレームの表示
    /********************************************************************/
    /* private methods : display                                        */
    /********************************************************************/
    // OnUpdate から順に呼ばれる。どれも「覚えた記録を読んで要素を有効にする」だけで、
    // 状態は書き換えない。表示のもとになる嘘/本物の判定は IsFake 1 か所に寄せてある。

    /// <summary>このデバフがこの人に付いたとき、嘘の回だったか。</summary>
    /// <remarks>記録が無ければ本物として扱う。分身を見逃した回で全部が嘘扱いになるより、
    /// 素直に出して食らう方がまだ立て直せるため。</remarks>
    private bool IsFake(uint objectId, uint[] statusIds) =>
        _fakeStatuses.ContainsAny(statusIds.Select(id => new StatusInfo(objectId, id)));

    /// <summary>白と黒、どちらの塔を取るかを床の円で出す。</summary>
    /// <remarks>白傷/黒傷はそれ自体が「反対の色を取れ」の意味。そのうえで
    ///   ・傷デバフ自体が嘘        -> 反転
    ///   ・「外せ」(Die) が本物    -> 反転
    ///   ・「取れ」(Live) が嘘     -> 反転
    /// と最大 3 回まで裏返る。裏返した回数が奇数なら白。</remarks>
    private void ShowWoundColor()
    {
        if(!BasePlayer.HasStatus([.. Debuff.WhiteWound, .. Debuff.BlackWound], out var status)) return;

        var showWhite = status[0].ID.EqualsAny(Debuff.WhiteWound);
        if(_fakeStatuses.Contains(new(BasePlayer.ObjectId, status[0].ID)))
            showWhite = !showWhite;
        if(BasePlayer.HasStatus(Debuff.Die) && !IsFake(BasePlayer.ObjectId, Debuff.Die))
            showWhite = !showWhite;
        if(BasePlayer.HasStatus(Debuff.Live) && _fakeStatuses.Contains(new(BasePlayer.ObjectId, Debuff.Live)))
            showWhite = !showWhite;

        Controller.GetElementByName(showWhite ? "White" : "Black")!.Enabled = true;
    }

    /// <summary>視線の予告。誰か 1 人でも近ければ出す。</summary>
    /// <remarks>視線は全員が同時に食らうので、他人のデバフでも残り時間は同じ。
    /// 自分に付いているときだけ、中央に寄る誘導 (MiddleGaze) も足す。</remarks>
    private void ShowGaze()
    {
        foreach(var member in Controller.GetPartyMembers())
        {
            if(!member.HasStatus(Debuff.LookAway, out var times, lessThan: C.LookDontlookTH)) continue;

            var inverted = IsFake(member.ObjectId, Debuff.LookAway);
            var gaze = Controller.GetElementByName(inverted ? "LookAt" : "LookAway");
            gaze.Enabled = true;
            gaze.overlayText = Text(inverted ? "LookAt" : "LookAway", times.SafeSelect(0).Time);
            Controller.GetElementByName("EyeScope").Enabled = true;

            if(!member.AddressEquals(BasePlayer)) continue;

            var middle = Controller.GetElementByName("MiddleGaze");
            middle.Enabled = true;
            middle.color = Controller.AttentionColor;
            // 円の中に入ったら線を消す。着いたことが一目で分かるように。
            middle.tether = Vector2.Distance(BasePlayer.Position.ToVector2(),
                middle.RefPosition.ToVector2()) > middle.radius;
        }
    }

    /// <summary>自分が散開側か頭割り側かを出す。</summary>
    /// <remarks>「本物の散開」と「嘘の頭割り」がどちらも散開。自分がそのどちらでもなければ
    /// 頭割り側なので、誰か 1 人ぶんの残り時間を借りて頭割りの位置を出す。</remarks>
    private void ShowStackOrSpread()
    {
        var spread = false;
        if(BasePlayer.HasStatus(Debuff.Stack, out var fakeStack, lessThan: C.StackSpreadTH) &&
           IsFake(BasePlayer.ObjectId, Debuff.Stack))
        {
            ShowStackSpreadMarker(stack: false, fakeStack.SafeSelect(0).Time);
            spread = true;
        }
        if(BasePlayer.HasStatus(Debuff.Spread, out var trueSpread, lessThan: C.StackSpreadTH) &&
           !IsFake(BasePlayer.ObjectId, Debuff.Spread))
        {
            ShowStackSpreadMarker(stack: false, trueSpread.SafeSelect(0).Time);
            spread = true;
        }
        if(spread) return;

        foreach(var member in Controller.GetPartyMembers())
        {
            if(member.HasStatus(Debuff.Stack, out var trueStack, lessThan: C.StackSpreadTH) &&
               !IsFake(member.ObjectId, Debuff.Stack))
            {
                ShowStackSpreadMarker(stack: true, trueStack.SafeSelect(0).Time);
                return;
            }
            if(member.HasStatus(Debuff.Spread, out var fakeSpread, lessThan: C.StackSpreadTH) &&
               IsFake(member.ObjectId, Debuff.Spread))
            {
                ShowStackSpreadMarker(stack: true, fakeSpread.SafeSelect(0).Time);
                return;
            }
        }
    }

    /// <summary>頭割り/散開の立ち位置マーカーを出す。</summary>
    /// <remarks>ロールごとに位置が違うので要素は 4 つある。さらに 1 回目と 2 回目で位置を
    /// 変えたい人向けに "_2" 付きを別に登録してあり、設定が OFF なら常に "_2" なしを使う。</remarks>
    private void ShowStackSpreadMarker(bool stack, float seconds)
    {
        var kind = stack ? "Stack" : "Spread";
        var role = BasePlayer.Job.IsDps() ? "DPS" : "Support";
        var second = C.DifferentiateFirstSecondStackSpread && !IsFirstStackSpread() ? "_2" : "";
        if(!Controller.TryGetElementByName($"{kind}{role}{second}", out var e)) return;

        e.Enabled = true;
        e.color = Controller.AttentionColor;
        e.overlayText = Text(kind, seconds);
    }

    /// <summary>1 回目の頭割り/散開か。</summary>
    /// <remarks>1 回目は 8 人中 5 人以上に頭割りか散開が付く。2 回目は 4 人以下しか付かない。
    /// この差だけで区別している。</remarks>
    private bool IsFirstStackSpread() =>
        Controller.GetPartyMembers().Count(x => x.HasStatus([.. Debuff.Spread, .. Debuff.Stack])) > 4;

    /// <summary>動くな/動けの予告文を返す。動きに合わせて枠を点滅させる。</summary>
    /// <remarks>残り 3 秒を切ったら点滅を倍速にする。数字を読まなくても切迫が分かるように。</remarks>
    private string ShowMoveOrDontMove()
    {
        if(!BasePlayer.HasStatus(Debuff.DontMove, out var times, lessThan: C.MoveDontmoveTH)) return "";

        var seconds = times.SafeSelect(0).Time;
        var dontMove = !IsFake(BasePlayer.ObjectId, Debuff.DontMove);
        if(Controller.TryGetLayoutByName(dontMove ? "DontMove" : "Move", out var layout))
            layout.Enabled = seconds > 3
                ? Environment.TickCount64 % 500 > 250
                : Environment.TickCount64 % 250 > 125;

        return Text(dontMove ? "DontMove" : "Move", seconds);
    }

    /// <summary>足元に置くのが範囲かドーナツかを、中央の誘導に出す。</summary>
    /// <remarks>炎とドーナツは互いの裏返しなので、嘘なら相手の文面になる。</remarks>
    private void ShowDropShape()
    {
        if(BasePlayer.HasStatus(Debuff.Donut, out var donut, lessThan: C.DonutAOETH))
            ShowMiddleDrop(Text(IsFake(BasePlayer.ObjectId, Debuff.Donut) ? "DropAOE" : "DropDonut",
                donut.SafeSelect(0).Time));

        if(BasePlayer.HasStatus(Debuff.FireSpread, out var fire, lessThan: C.DonutAOETH))
            ShowMiddleDrop(Text(IsFake(BasePlayer.ObjectId, Debuff.FireSpread) ? "DropDonut" : "DropAOE",
                fire.SafeSelect(0).Time));
    }

    /// <summary>中央へ置きに行く誘導。視線が近いあいだは出さない。</summary>
    /// <remarks>視線の最中に中央を見せると、視線を外す動きと真逆の指示になる。
    /// パーティの誰か 1 人でも 6.5 秒以内に視線が来るなら黙る。</remarks>
    private void ShowMiddleDrop(string text)
    {
        if(Controller.GetPartyMembers().Any(x => x.HasStatus(Debuff.LookAway, 6.5f))) return;

        var element = Controller.GetElementByName("MiddleDrop");
        element.Enabled = true;
        element.color = Controller.AttentionColor;
        element.overlayText = text;
    }

    /// <summary>Language レイアウトから表示文面を引き、"#" を残り秒数に置き換える。</summary>
    /// <remarks>文面はレイアウトの要素として登録してあるので、ユーザーが Registered layouts から
    /// 直接書き換えられる。設定をリセットするとレイアウトごと消えるため、引けなかったときは
    /// 黙るのではなく画面に「設定をリセットしろ」と出す。</remarks>
    private string Text(string element, float seconds)
    {
        var e = Controller.GetRegisteredLayouts().SafeSelect("Language")?.GetElement(element);
        return e == null
            ? "Text could not be retrieved, reset script's settings"
            : e.overlayTextIntl.Get(e.overlayText).Replace("#", $"{seconds:F1}");
    }

    #endregion

    #region private methods : デバフを受けた瞬間の通知
    /********************************************************************/
    /* private methods : announce                                       */
    /********************************************************************/
    // OnGainBuffEffect から呼ばれる。デバフが付いた 1 回だけ走る。
    // 表示 (毎フレーム) と違い、ここは「その瞬間の _isLie」で文面が決まる。
    // 色に生の数字が混ざっているのは UIColor に名前が無い色を使っているため。

    /// <summary>自分に付いたデバフを、裏返したあとの言葉でチャットに出す。</summary>
    /// <remarks>他人ぶんも出す設定 (ShowOthers) のときは呼ばれない。
    /// 頭上マーカーだけは OutputInChat と無関係に動く。文字を出さない設定でも
    /// マーカーは欲しい、という使い方があるため。</remarks>
    private void AnnounceSelf(FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        // 「本物の散開」か「嘘の頭割り」= 自分は散開。残り 60 秒超が遅い方の組。
        if((Debuff.Spread.Contains(status.StatusId) && !_isLie) ||
           (Debuff.Stack.Contains(status.StatusId) && _isLie))
        {
            var isLong = status.RemainingTime > 60f;
            MarkSelfSpread(isLong ? C.MarkingParamLongSpread : C.MarkingParamShortSpread);
            if(C.OutputInChat)
                Print(UIColor.Orange, (isLong ? C.LongSpread : C.ShortSpread).Get());
        }

        if(Debuff.LookAway.Contains(status.StatusId) && C.OutputInChat)
        {
            var isLong = status.RemainingTime > 65f;
            Print(_isLie ? (UIColor)16 : UIColor.Red, (isLong
                ? _isLie ? C.LongGazeInv : C.LongGaze
                : _isLie ? C.ShortGazeInv : C.ShortGaze).Get());
        }

        if(Debuff.DontMove.Contains(status.StatusId) && C.OutputInChat)
            Print(_isLie ? (UIColor)506 : UIColor.Yellow,
                (_isLie ? C.AccelerationBombInv : C.AccelerationBomb).Get());
    }

    /// <summary>炎/津波が範囲かドーナツかを出す。</summary>
    /// <remarks>他人ぶんを出す設定でもこれだけは自分ぶんが出る。形は自分の足元に置くものなので、
    /// 読み上げても他人の役に立たないため。</remarks>
    private void AnnounceSelfShape(FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        if(!C.OutputInChat) return;

        if(Debuff.FireSpread.Contains(status.StatusId))
            Print(_isLie ? (UIColor)563 : (UIColor)557, (_isLie ? C.FireIsDonut : C.FireIsAOE).Get());
        if(Debuff.Donut.Contains(status.StatusId))
            Print(_isLie ? (UIColor)555 : (UIColor)553, (_isLie ? C.WaterIsAOE : C.WaterIsDonut).Get());
    }

    /// <summary>散開が付いた自分に頭上マーカーを付ける。</summary>
    /// <remarks>サーバーへコマンドを送るので、リプレイ中は撃たずにログへ落とす。
    /// 1 秒に 1 本までに絞っているのは、同じ瞬間に複数のデバフが来てもマーカーが
    /// 連打にならないようにするため。</remarks>
    private void MarkSelfSpread(uint markingParam)
    {
        if(!C.UseSelfmark || markingParam == 0) return;
        if(!GenericHelpers.IsScreenReady() || !EzThrottler.Throttle("Chat", 1000)) return;

        UseCommand($"/marking {TextCommandParam.Get(markingParam).Param.GetText()} <me>");
    }

    /// <summary>読み上げ用の 1 件を、同じ文面ごとの箱に積む。</summary>
    /// <remarks>ここでは出さない。8 人ぶんが同じ瞬間に来るので、そのまま出すと同じ文が
    /// 8 行流れる。次の OnUpdate で名前をまとめて 1 行にして出す。</remarks>
    private void EnqueueOtherCallout(string name, FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        if(!C.OutputInChat) return;

        if((Debuff.Spread.Contains(status.StatusId) && !_isLie) ||
           (Debuff.Stack.Contains(status.StatusId) && _isLie))
            Enqueue(UIColor.Orange,
                (status.RemainingTime > 60f ? C.Other_LongSpread : C.Other_ShortSpread).Get(), name);

        if(Debuff.LookAway.Contains(status.StatusId))
        {
            var isLong = status.RemainingTime > 65f;
            Enqueue(_isLie ? (UIColor)16 : UIColor.Red, (isLong
                ? _isLie ? C.Other_LongGazeInv : C.Other_LongGaze
                : _isLie ? C.Other_ShortGazeInv : C.Other_ShortGaze).Get(), name);
        }

        if(Debuff.DontMove.Contains(status.StatusId))
            Enqueue(_isLie ? (UIColor)506 : UIColor.Yellow,
                (_isLie ? C.Other_AccelerationBombInv : C.Other_AccelerationBomb).Get(), name);

        void Enqueue(UIColor color, string text, string who) =>
            _otherInfoQueue.GetOrCreate((text, color)).Add(who);
    }

    /// <summary>ためた読み上げを、文面ごとに名前を並べて 1 行ずつ出す。</summary>
    /// <remarks>自分が含まれていれば先頭に寄せて囲む。8 人の名前の中から自分を探す手間を
    /// 省くため。デバフは同じ瞬間に付くので、次の OnUpdate まで待てば全員そろっている。</remarks>
    private void FlushOtherCallouts()
    {
        if(_otherInfoQueue.Count == 0) return;

        var me = BasePlayer.Name.ToString();
        foreach(var (key, names) in _otherInfoQueue)
        {
            if(names.Remove(me))
                names.Insert(0, $"[>[{me} (YOU)]<]");
            Print(key.Color, key.Text.Replace("$", names.Print()));
        }
        _otherInfoQueue.Clear();
    }

    /// <summary>自分にだけ見えるチャット行を出す。</summary>
    /// <remarks>Svc.Chat.Print はローカル表示なので、他人には届かないし送信もされない。
    /// チャンネル指定は見た目の色分けのためだけで、そのチャンネルには流れない。</remarks>
    private void Print(UIColor color, string msg)
    {
        var entry = new XivChatEntry
        {
            Message = new SeStringBuilder().AddUiForeground(msg, (ushort)color).Build(),
        };
        if(C.OverrideChatType != XivChatType.None)
            entry.Type = C.OverrideChatType;

        Svc.Chat.Print(entry);
    }

    /// <summary>コマンドを実際にサーバーへ送る。</summary>
    /// <remarks>リプレイ中は送らず警告に落とす。/marking は本物のチャットコマンドなので、
    /// 再生を見ているだけのつもりで実戦のマーカーを動かすと事故になる。
    /// 2〜4 秒のばらつきを入れているのは、8 人が同時に撃って弾かれるのを避けるため。</remarks>
    private void UseCommand(string cmd)
    {
        Controller.Schedule(() =>
        {
            if(Svc.Condition[ConditionFlag.DutyRecorderPlayback])
                DuoLog.Warning($"Would use command: {cmd}");
            else
                Chat.ExecuteCommand(cmd);
        }, 2000 + Random.Shared.Next(2000));
    }

    #endregion

    #region private methods : 終盤の立ち位置
    /********************************************************************/
    /* private methods : final resolution                               */
    /********************************************************************/
    // 最後の扇と落雷は、詠唱している技 ID の組み合わせで嘘か本物かが分かる。
    // そのあと本体のロックオンエフェクトでさらに裏返ることがあるので、2 段構えになっている。

    /// <summary>最後の扇と落雷について、避けるのか当たりに行くのかを 1 度だけ出す。</summary>
    /// <remarks>手順は 2 段:
    ///   1. 4 通りの詠唱のどれが 2 体そろっているかで、扇と落雷それぞれの真偽を決める
    ///   2. 47781 の詠唱中に本体へ付くロックオンのうち、古い方から 2 つを見て反転を掛ける
    /// 4 通りのうち 2 つ以上が同時に成立するのは詠唱が切り替わる境目のフレームだけなので、
    /// ちょうど 1 つのときしか採らない。判定が固まる前に食いつくのを防ぐため。</remarks>
    private void ResolveFinalDebuff()
    {
        var npcs = Svc.Objects.OfTypeIBattleNpc();
        var isFakeBlowout = npcs.Count(x => x.IsCasting(47771)) == 2 && npcs.Count(x => x.IsCasting(47774)) == 2;
        var isTrueBlowout = npcs.Count(x => x.IsCasting(47768)) == 2;
        var isTrueLightning = npcs.Count(x => x.IsCasting(47775)) == 2;
        var isFakeLightning = npcs.Count(x => x.IsCasting(47777)) == 2 && npcs.Count(x => x.IsCasting(47776)) == 2;
        if(Enumerable.Count([isFakeBlowout, isFakeLightning, isTrueBlowout, isTrueLightning], x => x) == 1)
        {
            if(isFakeBlowout) _isFakeBlowout = true;
            if(isFakeLightning) _isFakeLightning = true;
            if(isTrueBlowout) _isFakeBlowout = false;
            if(isTrueLightning) _isFakeLightning = false;
        }

        if(_falseAnnounced) return;
        // 詠唱開始から 1 秒待つのは、ロックオンのエフェクトが本体に付くのがそのあとだから。
        if(!npcs.Any(x => x.IsCasting(47781) && x.CurrentCastTime > 1)) return;

        var kefka = npcs.First(x => x.DataId == 18475 && x.IsTargetable);
        if(!AttachedInfo.TryGetVfx(kefka, out var vfx)) return;

        _falseAnnounced = true;
        var lockOns = vfx.Where(x => x.Key.Contains("/lockon/")).OrderBy(x => x.Value.AgeF).Take(2);
        var invertLightning = lockOns.Any(x => x.Key == "vfx/lockon/eff/m0462trg_c03c.avfx");
        var invertBlowout = lockOns.Any(x => x.Key == "vfx/lockon/eff/m0462trg_c05c.avfx");
        if(invertLightning) _isFakeLightning = !_isFakeLightning;
        if(invertBlowout) _isFakeBlowout = !_isFakeBlowout;
        PluginLog.Information($"Inverted lightning: {invertLightning}, inverted blowout: {invertBlowout}, " +
            $"final resolution: IsFakeBlowout={_isFakeBlowout}, IsFakeLightning={_isFakeLightning}");

        // どちらかが未確定のまま来ることがある。分身の詠唱を 1 度も観測できなかった回で、
        // 当てずっぽうを出すくらいなら黙る。上流はここで例外を投げていた。
        if(_isFakeLightning is not { } fakeLightning || _isFakeBlowout is not { } fakeBlowout) return;

        // 嘘 = 当たらない = そこに立つ。両方嘘なら重なった場所が唯一の安置になる。
        var message = (fakeLightning, fakeBlowout) switch
        {
            (true, true) => C.Final_InBoth,
            (true, false) => C.Final_InLightning,
            (false, true) => C.Final_InCone,
            (false, false) => C.Final_OutBoth,
        };
        if(C.FinalResolution && C.OutputInChat)
            Print(UIColor.Pink, message.Get());
    }

    #endregion

    #region private methods : 設定 UI
    /********************************************************************/
    /* private methods : settings UI                                    */
    /********************************************************************/
    // OnSettingsDraw から呼ばれる。3 つに割れているのは、畳むと 100 行を超えるため。

    /// <summary>振る舞いの設定。何を出すか、どれだけ手前から出すか。</summary>
    private void DrawBehaviourSettings()
    {
        ImGui.Checkbox("Different positions for first and second stack/spread",
            ref C.DifferentiateFirstSecondStackSpread);
        if(C.DifferentiateFirstSecondStackSpread)
            ImGuiEx.TextWrapped(ImGuiColors.DalamudRed,
                "   Go to Registered elements and adjust positions of elements with \"_2\" prefix for second set of spreads/stacks!!!");

        ImGui.Checkbox("Output your debuffs into local chat (for you only)", ref C.OutputInChat);
        if(C.OutputInChat)
        {
            ImGui.Indent();
            ImGui.SetNextItemWidth(200f);
            ImGuiEx.EnumCombo(
                "Override chat channel (it will NOT send it to that channel, still local only, only affects visual)",
                ref C.OverrideChatType);
            ImGui.Checkbox("Also output other player debuffs for callouts (text still visible for you only)",
                ref C.ShowOthers);
            ImGui.Unindent();
        }

        // CTRL 押しでしか入れられないのは、実際にサーバーへ /marking を撃つため。
        ImGuiEx.Checkbox("Self-mark spreads (dangerous)", ref C.UseSelfmark, enabled: C.UseSelfmark || ImGuiEx.Ctrl);
        ImGuiEx.Tooltip("Hold CTRL and click to enable");
        if(C.UseSelfmark)
        {
            DrawMarkingParam("Short spread", ref C.MarkingParamShortSpread);
            DrawMarkingParam("Long spread", ref C.MarkingParamLongSpread);
        }

        ImGui.SetNextItemWidth(150f);
        ImGuiEx.SliderFloat("Display stack/spread in advance, seconds", ref C.StackSpreadTH, 3, 20);
        ImGui.SetNextItemWidth(150f);
        ImGuiEx.SliderFloat("Display move/don't move in advance, seconds", ref C.MoveDontmoveTH, 3, 20);
        ImGui.SetNextItemWidth(150f);
        ImGuiEx.SliderFloat("Display look/don't look in advance, seconds", ref C.LookDontlookTH, 3, 20);
        ImGui.SetNextItemWidth(150f);
        ImGuiEx.SliderFloat("Display donut/AOE placement in advance, seconds", ref C.DonutAOETH, 3, 20);
    }

    /// <summary>チャットに出す文面。</summary>
    /// <remarks>"Other" は読み上げ用で、$ が名前に置き換わる。
    /// 終盤の 4 つは "Print final positions" が ON のときだけ使う。</remarks>
    private void DrawMessageSettings()
    {
        DrawMessage(C.AccelerationBomb, "Acceleration bomb, normal");
        DrawMessage(C.AccelerationBombInv, "Acceleration bomb, inverted");
        DrawMessage(C.LongGaze, "Long gaze (away)");
        DrawMessage(C.LongGazeInv, "Long gaze (at)");
        DrawMessage(C.ShortGaze, "Short gaze (away)");
        DrawMessage(C.ShortGazeInv, "Short gaze (at)");
        DrawMessage(C.LongSpread, "Long spread");
        DrawMessage(C.ShortSpread, "Short spread");
        DrawMessage(C.FireIsAOE, "Fire is AOE (real)");
        DrawMessage(C.FireIsDonut, "Fire is Donut (fake)");
        DrawMessage(C.WaterIsAOE, "Water is AOE (fake)");
        DrawMessage(C.WaterIsDonut, "Water is Donut (real)");

        DrawMessage(C.Other_AccelerationBomb, "Other: Acceleration bomb, normal");
        DrawMessage(C.Other_AccelerationBombInv, "Other: Acceleration bomb, inverted");
        DrawMessage(C.Other_LongGaze, "Other: Long gaze (away)");
        DrawMessage(C.Other_LongGazeInv, "Other: Long gaze (at)");
        DrawMessage(C.Other_ShortGaze, "Other: Short gaze (away)");
        DrawMessage(C.Other_ShortGazeInv, "Other: Short gaze (at)");
        DrawMessage(C.Other_LongSpread, "Other: Long spread");
        DrawMessage(C.Other_ShortSpread, "Other: Short spread");
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey,
            "'Other' messages are for callouts only; $ is replaced with the player name.");

        ImGui.Checkbox("Print final positions (beta)", ref C.FinalResolution);
        if(!C.FinalResolution) return;

        DrawMessage(C.Final_InBoth, "Final positions - in both attacks");
        DrawMessage(C.Final_InCone, "Final positions - in cone only");
        DrawMessage(C.Final_InLightning, "Final positions - in lightning strike only");
        DrawMessage(C.Final_OutBoth, "Final positions - outside both attacks");
    }

    /// <summary>文面 1 つぶんの編集欄。</summary>
    /// <remarks>InternationalString は class なので、渡した先で書き換えれば設定に残る。</remarks>
    private static void DrawMessage(InternationalString message, string label)
    {
        ImGui.SetNextItemWidth(200f);
        message.ImGuiEditNoDefault();
        ImGui.SameLine();
        ImGuiEx.Text(label);
    }

    /// <summary>/marking に渡すマーカーの選択欄。</summary>
    private static void DrawMarkingParam(string name, ref uint param)
    {
        // /marking の引数のうち、頭上に出る 16 種だけ。それ以外は選ばせない。
        uint[] valid = [80, 82, 84, 86, 88, 90, 92, 94, 96, 98, 100, 102, 104, 476, 478, 480];

        ImGui.PushID(name);
        ImGui.SetNextItemWidth(200f);
        if(ImGui.BeginCombo(name,
               param == 0 ? "Not set" : TextCommandParam.GetRef(param).ValueNullable?.Param.GetText(),
               ImGuiComboFlags.HeightLarge))
        {
            if(ImGui.Selectable("Not Set", param == 0))
                param = 0;
            foreach(var x in valid)
                if(ImGui.Selectable(TextCommandParam.Get(x).Param.GetText(), param == x))
                    param = x;
            ImGui.EndCombo();
        }
        ImGui.PopID();
    }

    /// <summary>いま何を「嘘」と認識しているかの一覧。</summary>
    /// <remarks>Export / Import は、リプレイで再現するときに嘘の記録だけを持ち込むためのもの。
    /// チェックボックスは手で状態を作って表示を確かめるためにある。</remarks>
    private void DrawDebug()
    {
        if(!ImGui.CollapsingHeader("Debug")) return;

        ImGuiEx.Checkbox("IsFakeBlowout", ref _isFakeBlowout);
        ImGuiEx.Checkbox("IsFakeLightning", ref _isFakeLightning);
        ImGuiEx.Checkbox("FalseAnnounced", ref _falseAnnounced);
        ImGui.Checkbox("IsLie", ref _isLie);

        if(ImGui.Button("Export"))
            GenericHelpers.Copy(JsonConvert.SerializeObject(_fakeStatuses));
        if(ImGui.Button("Import"))
            _fakeStatuses = JsonConvert.DeserializeObject<List<StatusInfo>>(GenericHelpers.Paste() ?? "")
                ?? throw new NullReferenceException();

        ImGuiEx.Text($"List: {Debuff.All.Print()}");
        ImGuiEx.Text($"Casters: {_isTruth.Select(x => $"{x.Key}: {x.Value}").Print("\n")}");
        ImGuiEx.Text($"Fakes: \n{_fakeStatuses.Select(x => $"{x.objectId.GetObject()} / {x.statusId} " +
            $"({Svc.Data.GetExcelSheet<Status>().GetRowOrDefault(x.statusId)?.Name})").Print("\n")}");
    }

    #endregion

    #region types
    /********************************************************************/
    /* types                                                            */
    /********************************************************************/
    /// <summary>「誰に付いたどのデバフか」の 1 件。嘘だったものだけを記録する。</summary>
    /// <remarks>Export / Import で JSON になるので、フィールド名は変えないこと。</remarks>
    private record struct StatusInfo(uint objectId, uint statusId);

    public class Config
    {
        // 何秒前から出すか
        public float StackSpreadTH = 8.5f;
        public float MoveDontmoveTH = 8f;
        public float LookDontlookTH = 10f;
        public float DonutAOETH = 10f;

        // 出し方
        public bool DifferentiateFirstSecondStackSpread = false;
        public bool OutputInChat = true;
        public bool ShowOthers = false;
        public bool FinalResolution = true;
        public XivChatType OverrideChatType = XivChatType.None;

        // 頭上マーカー。サーバーへコマンドを送るので既定は OFF
        public bool UseSelfmark = false;
        public uint MarkingParamShortSpread;
        public uint MarkingParamLongSpread;

        // 自分あての文面
        public InternationalString AccelerationBomb = new(en: "Acceleration bomb on YOU (DON'T MOVE)", jp: "加速度　とまる");
        public InternationalString AccelerationBombInv = new(en: "Inverted acceleration bomb on YOU (MOVE)", jp: "加速度　うごく");
        public InternationalString LongGaze = new(en: "LONG GAZE on YOU (Look Away)", jp: "遅　視線　みない");
        public InternationalString LongGazeInv = new(en: "LONG GAZE on YOU (Look At)", jp: "遅　視線　みる");
        public InternationalString ShortGaze = new(en: "SHORT GAZE on YOU (Look Away)", jp: "早　視線　みない");
        public InternationalString ShortGazeInv = new(en: "SHORT GAZE on YOU (Look At)", jp: "早　視線　みる");
        public InternationalString LongSpread = new(en: "LONG SPREAD on YOU", jp: "遅　散開");
        public InternationalString ShortSpread = new(en: "SHORT SPREAD on YOU", jp: "早　散開");
        public InternationalString FireIsAOE = new(en: "- Fire is AOE (real)", jp: "- 真　ほのお　AOE");
        public InternationalString FireIsDonut = new(en: "- Fire is Donut (fake)", jp: "- 嘘　ほのお　ドーナツ");
        public InternationalString WaterIsAOE = new(en: "- Water is AOE (fake)", jp: "- 嘘　つなみ　AOE");
        public InternationalString WaterIsDonut = new(en: "- Water is Donut (real)", jp: "- 真　つなみ　ドーナツ");

        // 読み上げ用の文面。$ が名前に置き換わる
        public InternationalString Other_AccelerationBomb = new(en: "> Acceleration bomb on $ (DON'T MOVE)", jp: "> 加速度　とまる　対象： $");
        public InternationalString Other_AccelerationBombInv = new(en: "> Inverted acceleration bomb on $ (MOVE)", jp: "> 加速度　うごく　対象： $");
        public InternationalString Other_LongGaze = new(en: "> LONG GAZE on $ (Look Away)", jp: "> 遅　視線　みない　対象： $");
        public InternationalString Other_LongGazeInv = new(en: "> LONG GAZE on $ (Look At)", jp: "> 遅　視線　みる　対象： $");
        public InternationalString Other_ShortGaze = new(en: "> SHORT GAZE on $ (Look Away)", jp: "> 早　視線　みない　対象： $");
        public InternationalString Other_ShortGazeInv = new(en: "> SHORT GAZE on $ (Look At)", jp: "> 早　視線　みる　対象： $");
        public InternationalString Other_LongSpread = new(en: "> LONG SPREAD on $", jp: "> 遅　散開　対象: $");
        public InternationalString Other_ShortSpread = new(en: "> SHORT SPREAD on $", jp: "> 早　散開　対象: $");

        // 終盤の立ち位置
        public InternationalString Final_InCone = new(en: "> STAND: IN CONE");
        public InternationalString Final_InLightning = new(en: "> STAND: IN LIGHTNING");
        public InternationalString Final_InBoth = new(en: "> STAND: IN BOTH");
        public InternationalString Final_OutBoth = new(en: "> STAND: OUT BOTH");
    }

    #endregion
}
