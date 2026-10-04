using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using ECommons.Hooks.ActionEffectTypes;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game;
using Splatoon.Memory;
using Splatoon.SplatoonScripting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace RedmoonsScripts.Duties.Dawntrail.Dancing_Mad;

/// <summary>
/// P4_LockFaces をデバッグするためのログを、トライごとにファイルへ落とす。
/// </summary>
/// <remarks>
/// P4_LockFaces は別アセンブリなので中身を覗けない。代わりに、同じ入力を同じ規則で受けて
/// 「P4_LockFaces はこう判断したはず」を写し取り、実際に起きたこと (誰が視線を食らったか) と並べる。
///
/// 記録するもの (行頭のタグ):
///   [LIFE]  OnReset / フェーズ開始技 49884 / ケフカが殴れるか (PhaseActive) の切り替わり。
///           P4_LockFaces はこのどれかで嘘の記録を捨てるか、記録をやめる
///   [CLONE] 分身 (19510/19507) の VFX と技。正直/嘘つきの分類と、今の回の isLie がここで決まる
///   [BUFF+] 呪詛の叫声が付いた瞬間。P4_LockFaces が嘘と記録したか + 2056 の Param との照合
///   [2056]  新生エクスデスに付く 2056 の Param。1122=本物 / 1121=嘘 (別系統の真偽。照合用)
///   [WAVE]  同時に切れる保持者の組をつかんだ
///   [SELF]  自分の向き・詠唱中か・DR のモジュール状態 (TraceIntervalMs ごと)
///   [FIRE]  視線の発動。8 人全員の向き、各発生源への角度、P4_LockFaces と同じ規則で出した期待の向き
///   [HIT]   発動前後の着弾。プレイヤーの技とオートアタックは除く
///   [PEN]   発動前後に付いたステータス (石化などのペナルティ候補)
///   [SUM]   予測 (向きと扇から見た成否) と実際 (被弾したか) の突き合わせ
///
/// SUM が一致し続けるなら、真偽の判定と扇 (±ConeHalfAngle) の仮定は正しい。全員の予測が反転して
/// いれば真偽の取り違え、境界付近だけずれるなら扇の幅を疑う。自分だけ期待の向きと実際がずれて
/// いれば、P4_LockFaces の固定が効いていない (SELF の DR 状態と cast を見る)。
///
/// ファイル・パスまわりは ScriptEventRecorder から切り出して貼ったもの。直すときは向こうを先に直す。
/// </remarks>
internal unsafe class P4_LockFacesLogger : SplatoonScript<P4_LockFacesLogger.Config>
{
    #region types
    /********************************************************************/
    /* types                                                            */
    /********************************************************************/
    private enum CheckLevel { Ok = 0, Warning, Error }

    /// <summary>ディレクトリのチェック結果。Messages は重い順に並ぶとは限らない。</summary>
    private sealed record PathCheck(CheckLevel Level, string FullPath, List<(CheckLevel Level, string Text)> Messages)
    {
        public bool Usable => Level != CheckLevel.Error;
    }

    /// <summary>波に含まれる保持者 1 人。</summary>
    private sealed class WaveHolder
    {
        public uint EntityId;
        public string Name = "";
        public uint StatusId;
        public float RemainingAtLatch;
        public bool Fake;          // P4_LockFaces の規則で嘘と判定されているか
        public bool Removed;       // デバフが消えたのを見たか
    }

    /// <summary>同時に切れる保持者の組 1 つぶんの記録。</summary>
    private sealed class Wave
    {
        public readonly List<WaveHolder> Holders = [];
        public long ExpectedFireMs;                                // つかんだ時点の残りから出した発動時刻
        public long FireMs;                                        // 実際に消えたのを見た時刻。0 = まだ
        public readonly Dictionary<uint, bool> PredictedFail = [];  // EID -> 向きと扇から見て食らうはずか
        public readonly Dictionary<uint, List<string>> Hits = [];   // EID -> 実際に食らった技
    }

    #endregion

    #region class
    /********************************************************************/
    /* class                                                            */
    /********************************************************************/
    public class Config
    {
        public string LogDirectory = "";
        public float CloseGraceSeconds = 5f;   // 戦闘終了から何秒後にファイルを閉じるか
        public float TraceLeadSeconds = 5f;    // 視線の何秒前から追い始めるか
        public int TraceIntervalMs = 100;      // 自分の向きを何ミリ秒ごとに書くか
        public float HitWindowSeconds = 2f;    // 発動から何秒間の着弾を視線の結果とみなすか
        public float ConeHalfAngle = 45f;      // 予測に使う扇の半角。正しい値は未確定なので変えられる
    }

    /// <summary>Daily Routines の AutoFaceCameraDirection が今有効かだけを見る。</summary>
    /// <remarks>P4_LockFaces の Api から IsModuleEnabled だけ抜いたもの。[EzIPC] はフィールドにだけ付ける
    /// (メソッドに付けると EzIPC が提供側として登録してしまう)。</remarks>
    public sealed class DrProbe
    {
        [EzIPC("IsModuleEnabled")] private Func<string, bool?> _isModuleEnabled = null!;
        private bool _initialized;

        public void Init()
        {
            if (_initialized) return;
            _initialized = true;
            EzIPC.Init(this, "DailyRoutines", SafeWrapper.AnyException);
        }

        /// <summary>true=有効 / false=無効 / null=Daily Routines が居ない。</summary>
        public bool? FaceModuleEnabled =>
            Svc.PluginInterface.InstalledPlugins.Any(x => x.IsLoaded && x.InternalName == "DailyRoutines")
                ? _isModuleEnabled("AutoFaceCameraDirection") : null;
    }

    #endregion

    #region const
    /********************************************************************/
    /* const                                                            */
    /********************************************************************/
    private static readonly uint[] LookAwayIds = [5543, 452];   // 呪詛の叫声 (P4_LockFaces と同じ)
    private static readonly uint[] CloneIds = [19510, 19507];   // 正直/嘘つきの分身

    #endregion

    #region public properties
    /********************************************************************/
    /* public properties                                                */
    /********************************************************************/
    public override HashSet<uint>? ValidTerritories { get; } = [1363];   // Dancing Mad (Ultimate)
    public override Metadata Metadata => new(1, "Redmoon");

    #endregion

    #region private properties
    /********************************************************************/
    /* private properties                                               */
    /********************************************************************/
    // ---- ファイル。EndTry で閉じる -------------------------------------------
    private StreamWriter? _writer;
    private string _fileName = "";
    private readonly Stopwatch _clock = new();
    private long _lines;
    private long _nextFlushMs;
    private long _closeAtMs;
    private int _tryNumber;
    private string _status = "待機中";
    private CheckLevel _statusLevel = CheckLevel.Ok;
    private string? _checkedPath;
    private PathCheck? _check;
    private bool _writeTestDone;
    private bool Recording => _writer != null;

    // ---- P4_LockFaces の状態の写し。P4_LockFaces が捨てる瞬間に同じように捨てる ----
    private readonly Dictionary<uint, bool> _isTruth = [];
    private readonly HashSet<(uint EntityId, uint StatusId)> _fakeStatuses = [];
    private bool _isLie;

    // ---- 照合と変化検出 -------------------------------------------------------
    private ushort? _last2056Param;
    private long _last2056Ms;
    private bool _lastPhaseActive;
    private bool? _lastDrEnabled;

    // ---- 波 ------------------------------------------------------------------
    private Wave? _wave;
    private long _lastWaveFireMs;          // 直前の波。消えかけのデバフでつかみ直さないため
    private long _nextTraceMs;
    private Vector3 _lastTracePos;
    private readonly List<(uint EntityId, string Name, uint StatusId, float Remaining)> _holders = [];

    private readonly DrProbe _dr = new();

    /// <summary>P4_LockFaces と同じ条件。これが false の間に付いたデバフを P4_LockFaces は記録しない。</summary>
    private bool PhaseActive => Svc.Objects.Any(x => x.BaseId == 18475 && x.IsTargetable);

    #endregion

    #region public methods
    /********************************************************************/
    /* public methods                                                   */
    /********************************************************************/

    public override void OnEnable() => _dr.Init();
    public override void OnDisable() => EndTry("スクリプト無効化");
    public override void OnCombatStart() => BeginTry();

    public override void OnCombatEnd()
    {
        Write("[LIFE] OnCombatEnd");
        if (Recording) _closeAtMs = Environment.TickCount64 + (long)(C.CloseGraceSeconds * 1000f);
    }

    public override void OnReset()
    {
        Write($"[LIFE] OnReset → P4_LockFaces はここで嘘の記録 {_fakeStatuses.Count} 件と分身 {_isTruth.Count} 体を捨てる");
        ClearMirror();
    }

    public override void OnUpdate()
    {
        if (!Recording) return;
        var now = Environment.TickCount64;
        if (_closeAtMs != 0 && now >= _closeAtMs) { EndTry("戦闘終了"); return; }

        WatchPhaseActive();
        if (_wave == null) TryLatchWave(now);
        else AdvanceWave(now);

        if (now < _nextFlushMs || !Recording) return;
        _nextFlushMs = now + 1000;
        Guard(() => _writer!.Flush());
    }

    public override void OnVFXSpawn(uint target, string vfxPath)
    {
        if (target.GetObject() is not { } obj || !CloneIds.Contains(obj.BaseId)) return;
        // P4_LockFaces.OnVFXSpawn と同じ分類
        bool? truth = vfxPath is "vfx/common/eff/z3oy_stlp7_c0c.avfx" or "vfx/common/eff/z3oy_stlp5_c0c.avfx" ? true
                    : vfxPath is "vfx/common/eff/z3oy_stlp6_c0c.avfx" or "vfx/common/eff/z3oy_stlp4_c0c.avfx" ? false
                    : null;
        if (truth is { } t) _isTruth[target] = t;
        Write($"[CLONE] VFX {vfxPath} on {Obj(obj)} → {(truth switch { true => "正直", false => "嘘つき", _ => "分類に関係なし" })}");
    }

    public override void OnActionEffectEvent(ActionEffectSet set)
    {
        if (set.Action is not { } action || set.Source is not { } source) return;

        if (CloneIds.Contains(source.BaseId))
        {
            // P4_LockFaces.OnActionEffectEvent と同じ。分類済みの分身だけが isLie を動かす
            var known = _isTruth.TryGetValue(source.EntityId, out var truth);
            if (known) _isLie = !truth;
            Write($"[CLONE] {action.Name}({action.RowId}) from {Obj(source)} → " +
                  (known ? $"{(truth ? "正直" : "嘘つき")}なので isLie={YesNo(_isLie)}" : "未分類の分身。isLie は変わらない"));
        }
        if (_wave != null) RecordHit(set, action);
    }

    public override void OnGainBuffEffect(uint sourceId, FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        if (status.StatusId == 2056) { Record2056(sourceId, status, "付与"); return; }
        if (LookAwayIds.Contains(status.StatusId)) { RecordShriek(sourceId, status); return; }
        if (_wave != null && sourceId.GetObject() is IPlayerCharacter pc)
            Write($"[PEN] {StatusName(status.StatusId)} param={status.Param} remain={status.RemainingTime:F2} " +
                  $"on {pc.Name} ({FromFire()})");
    }

    public override void OnUpdateBuffEffect(uint sourceId, FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        if (status.StatusId == 2056) Record2056(sourceId, status, "更新");
    }

    public override void OnRemoveBuffEffect(uint sourceId, FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        if (!LookAwayIds.Contains(status.StatusId) || _wave == null) return;
        var holder = _wave.Holders.FirstOrDefault(h => h.EntityId == sourceId);
        if (holder == null) return;
        holder.Removed = true;
        if (_wave.FireMs == 0) Fire("デバフが消えた");
    }

    public override void OnStartingCast(uint sourceId, PacketActorCast* packet)
    {
        if (packet->ActionType != (byte)ActionType.Action || packet->ActionID != 49884) return;
        Write($"[LIFE] フェーズ開始技 49884 → P4_LockFaces は Controller.Reset() で嘘の記録 {_fakeStatuses.Count} 件を捨てる");
        ClearMirror();
    }

    public override void OnSettingsDraw()
    {
        DrawDirectory();
        ImGui.Separator();
        ImGuiEx.Text(Color(_statusLevel), _status);
        if (Recording) ImGuiEx.Text($"経過 {_clock.Elapsed:mm\\:ss} / {_lines} 行");
        ImGuiEx.Text($"DR AutoFaceCameraDirection: {(_dr.FaceModuleEnabled switch { true => "有効", false => "無効", _ => "Daily Routines が居ない" })}");
        if (ImGuiEx.CollapsingHeader("Trace")) DrawTraceSettings();
    }

    #endregion

    #region private methods
    /********************************************************************/
    /* private methods                                                  */
    /********************************************************************/

    // ---- 真偽の写し ----------------------------------------------------------

    /// <summary>P4_LockFaces が記録を捨てるのと同じタイミングで、写しも捨てる。</summary>
    private void ClearMirror()
    {
        _isTruth.Clear();
        _fakeStatuses.Clear();
    }

    /// <summary>呪詛の叫声が付いた。P4_LockFaces が嘘と記録したか、2056 と合っているかを書く。</summary>
    /// <remarks>P4_LockFaces は PhaseActive でないときに付いたものを無視する。そうなっていれば必ず書く。
    /// sourceId は名前に反して「付いた側」(P4_LockFaces と同じ扱い)。付けた側は status.SourceObject。</remarks>
    private void RecordShriek(uint sourceId, FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        if (!sourceId.TryGetPlayer(out var pc)) return;
        var phase = PhaseActive;
        if (phase && _isLie) _fakeStatuses.Add((sourceId, status.StatusId));
        var mirror = !phase ? "P4_LockFaces は無視する (PhaseActive=N)" : _isLie ? "嘘として記録" : "本物 (記録なし)";

        var cross = "2056: 未観測";
        if (_last2056Param is { } param)
        {
            bool? truth2056 = param switch { 1122 => true, 1121 => false, _ => null };
            var age = (Environment.TickCount64 - _last2056Ms) / 1000f;
            cross = truth2056 is not { } t ? $"2056: param={param} (1122/1121 以外) {age:F1}s 前"
                  : $"2056: {(t ? "本物" : "嘘")} {age:F1}s 前 → {(t == !_isLie ? "一致" : "*** 不一致 ***")}";
        }
        Write($"[BUFF+] {StatusName(status.StatusId)} on {Obj(pc)} remain={status.RemainingTime:F2} " +
              $"from=0x{status.SourceObject.ObjectId:X8} isLie={YesNo(_isLie)} → {mirror} / {cross}");
    }

    private void Record2056(uint targetId, FFXIVClientStructs.FFXIV.Client.Game.Status status, string kind)
    {
        if (targetId.GetObject() is not { BaseId: 19510 } obj) return;
        _last2056Param = status.Param;
        _last2056Ms = Environment.TickCount64;
        var meaning = status.Param switch { 1122 => "本物", 1121 => "嘘", _ => "?" };
        Write($"[2056] {kind} param={status.Param} ({meaning}) on {Obj(obj)}");
    }

    /// <summary>ケフカが殴れるかどうか (= P4_LockFaces の PhaseActive) が変わったら書く。</summary>
    private void WatchPhaseActive()
    {
        var active = PhaseActive;
        if (active == _lastPhaseActive) return;
        _lastPhaseActive = active;
        Write($"[LIFE] PhaseActive={YesNo(active)} (ケフカ 18475 が殴れる{(active ? "" : "ない")})");
    }

    // ---- 波 ------------------------------------------------------------------

    /// <summary>次の波が TraceLeadSeconds 以内に来るなら、その保持者をつかむ。</summary>
    /// <remarks>まとめ方は P4_LockFaces.TryLatchWave と同じ (一番早い人から 2 秒以内)。
    /// 嘘かどうかもこの時点の写しで確定させる。発動するころにはデバフが消えていて引けないため。</remarks>
    private void TryLatchWave(long now)
    {
        _holders.Clear();
        foreach (var pc in Controller.GetPartyMembers())
            foreach (var s in pc.StatusList)
            {
                if (!LookAwayIds.Contains(s.StatusId) || s.RemainingTime <= 0f) continue;
                _holders.Add((pc.EntityId, pc.Name.ToString(), s.StatusId, s.RemainingTime));
                break;
            }
        if (_holders.Count == 0) return;

        var first = _holders.Min(h => h.Remaining);
        if (first > C.TraceLeadSeconds) return;
        var expected = now + (long)(first * 1000f);
        if (Math.Abs(expected - _lastWaveFireMs) < 1500) return;

        _wave = new Wave { ExpectedFireMs = expected };
        foreach (var h in _holders.Where(h => h.Remaining <= first + 2f))
            _wave.Holders.Add(new WaveHolder
            {
                EntityId = h.EntityId, Name = h.Name, StatusId = h.StatusId, RemainingAtLatch = h.Remaining,
                Fake = _fakeStatuses.Contains((h.EntityId, h.StatusId)),
            });
        _lastWaveFireMs = expected;
        _nextTraceMs = now;
        _lastDrEnabled = null;
        Write($"[WAVE] 発動まで {first:F2}s / " +
              string.Join(", ", _wave.Holders.Select(h => $"{h.Name}={(h.Fake ? "嘘=見る" : "本物=見ない")}")));
    }

    /// <summary>波を 1 フレーム進める。自分の追跡 → 発動の取りこぼし救済 → 着弾待ちの終了。</summary>
    private void AdvanceWave(long now)
    {
        var wave = _wave!;
        if (now >= _nextTraceMs)
        {
            _nextTraceMs = now + Math.Max(16, C.TraceIntervalMs);
            TraceSelf(now);
        }
        // 消えたのを見逃したとき (死亡・範囲外など) は予定時刻の 1 秒後に発動扱いにする
        if (wave.FireMs == 0 && now > wave.ExpectedFireMs + 1000) Fire("予定時刻を過ぎた (消えたのを見ていない)");
        if (wave.FireMs != 0 && now > wave.FireMs + (long)(C.HitWindowSeconds * 1000f))
        {
            Summarize(wave);
            _wave = null;
        }
    }

    /// <summary>自分の向き・詠唱中か・移動・DR のモジュール状態を 1 行。</summary>
    /// <remarks>P4_LockFaces が向きを固定できているかは、ここで R が期待の値に張り付くかで分かる。
    /// DR は詠唱中に向きを送らないので cast=Y の行が発動の瞬間にあれば、それが原因。</remarks>
    private void TraceSelf(long now)
    {
        if (BasePlayer is not { } me) return;
        var dr = _dr.FaceModuleEnabled;
        var drText = dr switch { true => "on", false => "off", _ => "なし" };
        if (dr != _lastDrEnabled)
        {
            drText += " (変化)";
            _lastDrEnabled = dr;
        }
        var moved = Vector2.Distance(Flat(me.Position), Flat(_lastTracePos));
        _lastTracePos = me.Position;
        var t = (now - _wave!.ExpectedFireMs) / 1000f;
        Write($"[SELF] t{t:+0.00;-0.00} R={me.Rotation:+0.000;-0.000} cast={YesNo(me.IsCasting)}" +
              $"{(me.IsCasting ? $"({me.CastActionId})" : "")} moved={moved:F2} DR={drText}");
    }

    /// <summary>視線の発動。8 人全員について、向き・各発生源への角度・期待の向きを書く。</summary>
    private void Fire(string reason)
    {
        var wave = _wave!;
        wave.FireMs = Environment.TickCount64;
        var lag = (wave.FireMs - wave.ExpectedFireMs) / 1000f;
        Write($"[FIRE] {reason} / 予定との差 {lag:+0.00;-0.00}s / 扇 ±{C.ConeHalfAngle:F0}°");
        foreach (var pc in Controller.GetPartyMembers()) SnapshotMember(wave, pc);
    }

    /// <summary>1 人ぶん。自分以外の保持者を発生源にして、見る/見ないを満たしているかを出す。</summary>
    /// <remarks>期待の向きは P4_LockFaces.ComputeFacing と同じ規則 (ExpectedFacing)。
    /// Δ は実際の向きと期待の向きのずれ。自分の Δ が大きければ固定が効いていない。</remarks>
    private void SnapshotMember(Wave wave, IPlayerCharacter pc)
    {
        var myPos = Flat(pc.Position);
        var facing = Facing(pc.Rotation);
        var isHolder = wave.Holders.Any(h => h.EntityId == pc.EntityId);
        var sources = wave.Holders
            .Where(h => h.EntityId != pc.EntityId && h.EntityId.GetObject() is not null)
            .Select(h => (Holder: h, Pos: Flat(h.EntityId.GetObject()!.Position)))
            .ToList();

        var expected = ExpectedFacing(myPos, sources.Select(s => (s.Pos, s.Holder.Fake)));
        var expectedText = expected is { } e
            ? $"期待 R={MathF.Atan2(e.X, e.Y):+0.000;-0.000} Δ={AngleBetween(facing, e):F1}°"
            : "期待 なし (発生源が見えない)";

        var fail = false;
        var lines = new List<string>();
        foreach (var (holder, pos) in sources)
        {
            var angle = AngleBetween(facing, Vector2.Normalize(pos - myPos));
            var ok = (angle <= C.ConeHalfAngle) == holder.Fake;
            fail |= !ok;
            lines.Add($"        ← {holder.Name,-16} {(holder.Fake ? "嘘=見る  " : "本物=見ない")} " +
                      $"{angle,6:F1}° {(angle <= C.ConeHalfAngle ? "扇内" : "扇外")} {(ok ? "OK" : "NG")}");
        }
        wave.PredictedFail[pc.EntityId] = fail;

        var self = pc.EntityId == BasePlayer?.EntityId ? " ★自分" : "";
        Write($"    {pc.Name,-16} {Job(pc)} P={Vec(pc.Position)} R={pc.Rotation:+0.000;-0.000} " +
              $"cast={YesNo(pc.IsCasting)} {(isHolder ? "保持者" : "非保持者")} {expectedText} → 予測 {(fail ? "NG" : "OK")}{self}");
        foreach (var line in lines) WriteRaw(line);
    }

    /// <summary>発動前後の着弾を記録する。プレイヤーの技とオートアタックは除く。</summary>
    /// <remarks>視線の技 ID は分かっていないので、ここでは絞らずに全部書いて SUM で名前ごと並べる。
    /// パーティ全員に当たったものは全体攻撃とみなし、成否の判定には使わない。</remarks>
    private void RecordHit(ActionEffectSet set, Lumina.Excel.Sheets.Action action)
    {
        if (action.IsPlayerAction || action.ActionCategory.RowId == 1) return;
        // TargetID は 64bit。上位に種別が乗ることがあるので、公式スクリプトと同じく下位 32bit で照合する
        var party = Controller.GetPartyMembers().ToDictionary(p => p.EntityId, p => p);
        var hit = set.TargetEffects.Where(t => party.ContainsKey((uint)t.TargetID)).ToList();
        if (hit.Count == 0) return;

        var raidwide = hit.Count >= party.Count;
        var detail = string.Join(" / ", hit.Select(t => $"{party[(uint)t.TargetID].Name}[{Effects(t)}]"));
        Write($"[HIT] {action.Name}({action.RowId}) from {Obj(set.Source)} ({FromFire()})" +
              $"{(raidwide ? " 全員に命中 = 全体攻撃とみなす" : "")}: {detail}");
        if (raidwide) return;

        var wave = _wave!;
        var refMs = wave.FireMs != 0 ? wave.FireMs : wave.ExpectedFireMs;
        if (Environment.TickCount64 < refMs - 1000) return;   // 発動の 1 秒より前は別ギミック
        foreach (var t in hit)
        {
            var id = (uint)t.TargetID;
            if (!wave.Hits.TryGetValue(id, out var list)) wave.Hits[id] = list = [];
            list.Add($"{action.Name}({action.RowId})");
        }
    }

    /// <summary>予測と実際を 1 人 1 行で突き合わせる。</summary>
    private void Summarize(Wave wave)
    {
        Write($"[SUM] 保持者: {string.Join(", ", wave.Holders.Select(h => $"{h.Name}={(h.Fake ? "嘘" : "本物")}"))}" +
              $" / 扇 ±{C.ConeHalfAngle:F0}°");
        int agree = 0, disagree = 0;
        foreach (var pc in Controller.GetPartyMembers())
        {
            var predicted = wave.PredictedFail.TryGetValue(pc.EntityId, out var f) ? f : (bool?)null;
            var hits = wave.Hits.TryGetValue(pc.EntityId, out var list) ? list : [];
            var verdict = predicted is not { } p ? "予測なし" : p == (hits.Count > 0) ? "一致" : "*** 不一致 ***";
            if (verdict == "一致") agree++; else if (predicted != null) disagree++;
            WriteRaw($"    {pc.Name,-16} 予測 {(predicted switch { true => "NG", false => "OK", _ => "-" })}  " +
                     $"被弾 {(hits.Count == 0 ? "-" : string.Join(", ", hits.Distinct()))}  {verdict}");
        }
        WriteRaw($"    一致 {agree} / 不一致 {disagree}");
        WriteRaw("");
    }

    /// <summary>P4_LockFaces.ComputeFacing と同じ規則で、向くべき方向を単位ベクトルで返す。</summary>
    /// <remarks>嘘が 1 人でも居れば嘘の全員への二等分線、全員本物ならその反対。真反対で決まらないときは
    /// 見ない → 直角、見る → 近い方。P4_LockFaces を直したら、ここも同じに直すこと。</remarks>
    private static Vector2? ExpectedFacing(Vector2 myPos, IEnumerable<(Vector2 Pos, bool Fake)> sources)
    {
        Vector2 look = default, away = default, nearestLook = default, firstAway = default;
        int lookCount = 0, awayCount = 0;
        var nearest = float.MaxValue;
        foreach (var (pos, fake) in sources)
        {
            var delta = pos - myPos;
            var distance = delta.Length();
            if (distance < 0.01f) continue;
            var unit = delta / distance;
            if (fake)
            {
                look += unit;
                lookCount++;
                if (distance < nearest) { nearest = distance; nearestLook = unit; }
            }
            else
            {
                away += unit;
                if (awayCount++ == 0) firstAway = unit;
            }
        }
        if (lookCount > 0) return Vector2.Normalize(look.LengthSquared() > 0.0001f ? look : nearestLook);
        if (awayCount > 0) return Vector2.Normalize(away.LengthSquared() > 0.0001f ? -away : new Vector2(firstAway.Y, -firstAway.X));
        return null;
    }

    // ---- ファイル --------------------------------------------------------------

    /// <summary>新しいトライのファイルを開く。パスが使えなければ開かない。</summary>
    private void BeginTry()
    {
        EndTry("次のトライが始まった");
        _tryNumber++;
        _wave = null;
        _lastPhaseActive = false;

        var check = RefreshCheck(C.LogDirectory, writeTest: true);
        if (!check.Usable)
        {
            SetStatus(CheckLevel.Error, $"トライ {_tryNumber} は記録しない: {check.Messages.First(m => m.Level == CheckLevel.Error).Text}");
            return;
        }

        var replay = Svc.Condition[ConditionFlag.DutyRecorderPlayback];
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}_P4LF_try{_tryNumber:D2}{(replay ? "_replay" : "")}.log";
        var path = Path.Combine(check.FullPath, name);
        Guard(() =>
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
            _writer = new StreamWriter(stream, new UTF8Encoding(false), 65536);
        });
        if (!Recording) return;

        _fileName = name;
        _lines = 0;
        _closeAtMs = 0;
        _nextFlushMs = Environment.TickCount64 + 1000;
        _clock.Restart();
        WriteHeader(replay);
        Write("[LIFE] OnCombatStart");
        SetStatus(CheckLevel.Ok, $"記録中: {name}");
    }

    /// <summary>ファイルを閉じる。開いていなければ何もしない。</summary>
    private void EndTry(string reason)
    {
        if (!Recording) return;
        if (_wave != null) Summarize(_wave);
        _wave = null;
        Write($"--- 記録終了 ({reason}) / {_lines + 1} 行");
        if (_writer is { } writer) Guard(() => writer.Dispose());
        _writer = null;
        _clock.Stop();
        _closeAtMs = 0;
        SetStatus(CheckLevel.Ok, $"保存した: {_fileName} ({reason})");
    }

    private void WriteHeader(bool replay)
    {
        WriteRaw($"# P4_LockFacesLogger v{Metadata.Version}");
        WriteRaw($"# 開始   : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  トライ {_tryNumber}{(replay ? " (duty recorder 再生中)" : "")}");
        WriteRaw($"# 自分   : {Obj(BasePlayer)} {Job(BasePlayer)}");
        foreach (var pc in Controller.GetPartyMembers())
            WriteRaw($"# パーティ: {Obj(pc)} {Job(pc)}");
        WriteRaw($"# 設定   : TraceLead={C.TraceLeadSeconds}s Trace={C.TraceIntervalMs}ms HitWindow={C.HitWindowSeconds}s Cone=±{C.ConeHalfAngle}°");
        WriteRaw("# R      : IGameObject.Rotation (ラジアン)。0=南 / +π/2=東 / ±π=北。方位ではない");
        WriteRaw("# 角度   : 各人の向きから発生源までの角度 (0〜180°)。嘘=扇内なら OK / 本物=扇外なら OK");
        WriteRaw("# SELF t : 予定発動時刻からの秒。負=発動前");
        WriteRaw("");
    }

    private void Write(string line)
    {
        if (!Recording) return;
        var t = _clock.Elapsed;
        WriteRaw($"+{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3} {line}");
    }

    private void WriteRaw(string line)
    {
        if (!Recording) return;
        Guard(() => _writer!.WriteLine(line));
        _lines++;
    }

    /// <summary>ファイル操作を 1 回だけ試す。失敗したらこのトライの記録をやめる。</summary>
    /// <remarks>ディスクが満杯・USB を抜いた・権限が変わった、といった失敗は 1 回起きると
    /// その後の全イベントで起き続ける。毎フレーム例外を出してログを埋めるより、止めて画面に出す。
    /// 例外を握るのはここ 1 か所だけ。</remarks>
    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            SetStatus(CheckLevel.Error, $"書き込みに失敗したので記録をやめた: {e.Message}");
            try { _writer?.Dispose(); } catch (Exception) { }
            _writer = null;
            _checkedPath = null;   // 次に見るときにパスを調べ直す
        }
    }

    // ---- パスのチェック (ScriptEventRecorder から) ----------------------------

    /// <summary>パスの文字列が変わっていればチェックし直す。書き込み試験は頼まれたときだけ。</summary>
    /// <remarks>設定画面は毎フレーム描かれるので、ディスクに触る試験を毎フレームやらない。</remarks>
    private PathCheck RefreshCheck(string raw, bool writeTest)
    {
        if (_check == null || _checkedPath != raw || (writeTest && !_writeTestDone))
        {
            _check = CheckPath(raw, writeTest);
            _checkedPath = raw;
            _writeTestDone = writeTest;
        }
        return _check;
    }

    /// <summary>ログを落とすディレクトリとして使えるかを調べる。</summary>
    /// <remarks>
    /// 上から順に、使えないものを弾いてから注意を足す:
    ///   エラー (記録しない)
    ///     空 / 使えない文字 / 相対パス / 解釈できない / ファイルを指している / 存在しない / 書き込めない
    ///   注意 (記録はする)
    ///     ドライブ直下 / ネットワーク上 / 空き容量が 100MB 未満
    /// 相対パスを弾くのは、基準がゲームの作業ディレクトリになり、どこに落ちたか分からなくなるため。
    /// ネットワークを注意にするのは、書き込みがゲームスレッドで行われ、詰まるとフレームが止まるため。
    /// 両端の引用符は外す。エクスプローラーの「パスのコピー」は "C:\..." の形で入るため。
    /// </remarks>
    private static PathCheck CheckPath(string raw, bool writeTest)
    {
        var messages = new List<(CheckLevel, string)>();
        PathCheck Fail(string text, string full = "")
        {
            messages.Add((CheckLevel.Error, text));
            return new(CheckLevel.Error, full, messages);
        }

        var path = raw.Trim().Trim('"').Trim();
        if (path.Length == 0) return Fail("パスが空");
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return Fail("パスに使えない文字が入っている");
        if (!Path.IsPathFullyQualified(path)) return Fail("絶対パスで指定する (例: D:\\SplatoonLogs)");

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail($"パスとして解釈できない: {e.Message}");
        }

        if (File.Exists(full)) return Fail("ディレクトリではなくファイルを指している", full);
        if (!Directory.Exists(full)) return Fail("ディレクトリが存在しない", full);
        if (writeTest && TryWriteTest(full) is { } error) return Fail($"書き込めない: {error}", full);

        var root = Path.GetPathRoot(full) ?? "";
        if (string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            messages.Add((CheckLevel.Warning, "ドライブ直下。トライごとにファイルが増えるので、専用のフォルダを勧める"));
        AddDriveWarnings(full, root, messages);

        messages.Add((CheckLevel.Ok, writeTest ? "書き込み試験 OK" : "形式 OK (書き込み試験は未実施)"));
        var level = messages.Any(m => m.Item1 == CheckLevel.Warning) ? CheckLevel.Warning : CheckLevel.Ok;
        return new(level, full, messages);
    }

    /// <summary>ネットワーク上か、空き容量が少ないかを注意として足す。</summary>
    private static void AddDriveWarnings(string full, string root, List<(CheckLevel, string)> messages)
    {
        const string networkWarning = "ネットワーク上。書き込みが詰まるとゲームが止まる。ローカルを勧める";
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            messages.Add((CheckLevel.Warning, networkWarning));
            return;
        }
        try
        {
            var drive = new DriveInfo(root);
            if (drive.DriveType == DriveType.Network) messages.Add((CheckLevel.Warning, networkWarning));
            if (drive.IsReady && drive.AvailableFreeSpace < 100L * 1024 * 1024)
                messages.Add((CheckLevel.Warning, $"空き容量が少ない ({drive.AvailableFreeSpace / 1024 / 1024} MB)"));
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            messages.Add((CheckLevel.Warning, $"ドライブの情報が取れない: {e.Message}"));
        }
    }

    /// <summary>実際に 1 ファイル作って消してみる。書けなければ理由を返す。</summary>
    /// <remarks>Directory.Exists が true でも、権限・読み取り専用メディア・同期ソフトのロックで
    /// 書けないことがある。確実なのは書いてみることだけ。</remarks>
    private static string? TryWriteTest(string directory)
    {
        var probe = Path.Combine(directory, $".ser_write_test_{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                fs.WriteByte(0);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    // ---- 行の部品 --------------------------------------------------------------

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    /// <summary>Rotation から向きの単位ベクトル (X, Z)。0=南 → (0, +1)。</summary>
    private static Vector2 Facing(float rotation) => new(MathF.Sin(rotation), MathF.Cos(rotation));

    private static float AngleBetween(Vector2 a, Vector2 b) =>
        MathF.Acos(Math.Clamp(Vector2.Dot(a, b), -1f, 1f)) * 180f / MathF.PI;

    private static string Obj(IGameObject? obj) =>
        obj == null ? "-" : $"{obj.Name}[DID:0x{obj.BaseId:X} EID:0x{obj.EntityId:X8} P={Vec(obj.Position)} R={obj.Rotation:+0.000;-0.000}]";

    private static string Vec(Vector3 v) => $"({v.X:F2},{v.Y:F2},{v.Z:F2})";

    private static string Job(IGameObject? obj) =>
        obj is IPlayerCharacter pc ? pc.ClassJob.ValueNullable?.Abbreviation.ToString() ?? "?" : "";

    private static string YesNo(bool value) => value ? "Y" : "N";

    private static string StatusName(uint id) =>
        $"{Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>().GetRowOrDefault(id)?.Name.ToString() ?? "?"}({id})";

    /// <summary>1 人ぶんの効果。Nothing は捨てる。ステータス付与は名前に引く。</summary>
    private static string Effects(TargetEffect target)
    {
        var parts = new List<string>();
        target.ForEach(e =>
        {
            if (e.type == ActionEffectType.Nothing) return;
            parts.Add(e.type is ActionEffectType.ApplyStatusEffectTarget or ActionEffectType.ApplyStatusEffectSource
                ? $"{e.type}:{StatusName(e.value)}"
                : $"{e.type}:{e.value}");
        });
        return string.Join(" ", parts);
    }

    /// <summary>今が発動から何秒か (発動前なら予定時刻から)。</summary>
    private string FromFire()
    {
        if (_wave == null) return "波の外";
        var reference = _wave.FireMs != 0 ? _wave.FireMs : _wave.ExpectedFireMs;
        return $"発動{(_wave.FireMs != 0 ? "" : "予定")}から {(Environment.TickCount64 - reference) / 1000f:+0.00;-0.00}s";
    }

    private void SetStatus(CheckLevel level, string text)
    {
        _statusLevel = level;
        _status = text;
    }

    // ---- 設定画面 --------------------------------------------------------------

    /// <summary>ディレクトリの入力とチェック結果。</summary>
    private void DrawDirectory()
    {
        ImGui.SetNextItemWidth(420f);
        ImGui.InputText("Log directory", ref C.LogDirectory, 512);
        var check = RefreshCheck(C.LogDirectory, writeTest: false);

        if (ImGui.Button("Check (write test)")) _check = null;   // 次の Refresh で試験込みでやり直す
        if (_check == null) check = RefreshCheck(C.LogDirectory, writeTest: true);

        var missing = check.FullPath != "" && !Directory.Exists(check.FullPath) && !File.Exists(check.FullPath);
        if (missing)
        {
            ImGui.SameLine();
            if (ImGui.Button("Create folder"))
            {
                // Guard は使わない。あれは失敗したら記録中のファイルまで閉じる
                try { Directory.CreateDirectory(check.FullPath); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    SetStatus(CheckLevel.Error, $"フォルダを作れなかった: {e.Message}");
                }
                _check = null;
                RefreshCheck(C.LogDirectory, writeTest: true);   // 作れたら、そのまま書けるかまで見る
            }
        }
        if (check.Usable && check.FullPath != "")
        {
            ImGui.SameLine();
            if (ImGui.Button("Open")) GenericHelpers.ShellStart(check.FullPath);
        }

        foreach (var (level, text) in check.Messages)
            ImGuiEx.Text(Color(level), $"  {Mark(level)} {text}");
        if (check.FullPath != "") ImGuiEx.Text(EColor.White, $"  → {check.FullPath}");

        ImGui.SetNextItemWidth(150f);
        ImGui.SliderFloat("Close after combat end (s)", ref C.CloseGraceSeconds, 0f, 30f, "%.0f");
        ImGuiEx.Tooltip("戦闘終了のあとに来る Wipe / Complete などを拾うための猶予");
    }

    /// <summary>追跡と判定の設定。</summary>
    private void DrawTraceSettings()
    {
        ImGui.SetNextItemWidth(150f);
        ImGui.SliderFloat("Trace from (s before)", ref C.TraceLeadSeconds, 1f, 10f, "%.0f");
        ImGui.SetNextItemWidth(150f);
        ImGui.SliderInt("Trace interval (ms)", ref C.TraceIntervalMs, 16, 500);
        ImGui.SetNextItemWidth(150f);
        ImGui.SliderFloat("Hit window (s after)", ref C.HitWindowSeconds, 0.5f, 5f, "%.1f");
        ImGui.SetNextItemWidth(150f);
        ImGui.SliderFloat("Cone half angle (deg)", ref C.ConeHalfAngle, 15f, 90f, "%.0f");
        ImGuiEx.Tooltip("予測に使う扇の半角。P4_Debuff_Reminder の EyeScope は 45。SUM の不一致が境界付近に寄るなら変えて比べる");
    }

    private static System.Numerics.Vector4 Color(CheckLevel level) => level switch
    {
        CheckLevel.Ok => EColor.GreenBright,
        CheckLevel.Warning => EColor.YellowBright,
        _ => EColor.RedBright,
    };

    private static string Mark(CheckLevel level) => level switch
    {
        CheckLevel.Ok => "OK",
        CheckLevel.Warning => "注意",
        _ => "NG",
    };

    #endregion
}
