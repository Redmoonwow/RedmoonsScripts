using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Hooks;
using ECommons.Hooks.ActionEffectTypes;
using ECommons.ImGuiMethods;
using ECommons.Schedulers;
using Splatoon.Memory;
using Splatoon.SplatoonScripting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace RedmoonsScripts.Generic;

/// <summary>
/// スクリプトのデバッグ用に、Splatoon が流すイベントをトライごとにファイルへ書き出す。
/// </summary>
/// <remarks>
/// ScriptEventLogger を元にしたもの。拾うイベントと絞り込みは同じで、違いは出力先:
///   ScriptEventLogger  : dalamud.log に全部混ざる。他のプラグインの行に埋もれる
///   ScriptEventRecorder: 指定したディレクトリに、戦闘 1 回 = ファイル 1 本で落とす
///
/// 1 トライ = OnCombatStart から OnCombatEnd まで。戦闘終了のあとに来る Wipe / Complete の
/// ディレクタ更新も拾えるよう、閉じるのは CloseGraceSeconds 待ってから。
/// duty recorder の再生中も同じように落ちる (ファイル名に _replay が付く)。録画を流して
/// ログを取り、スクリプトの判断と突き合わせるのが主な使い方。
///
/// 各行の先頭はトライ開始からの経過時間。オブジェクトは
///   名前[DID:0x.. EID:0x.. P=(x,y,z) R=±0.000]
/// の形で出す。R は IGameObject.Rotation そのもの (ラジアン、0=南 / +π/2=東 / ±π=北)。
/// コンパス方位ではないので読み違えないこと (ffxiv-coordinates §11)。
/// 詠唱はパケット経路 [pkt] とメモリ監視経路 [mem] の両方を出す。前者はサーバの値、後者は
/// 次のフレームにクライアントが補間した値で、向きがずれることがある。
///
/// 書き込みはゲームスレッドで行う。64KB のバッファに溜めて 1 秒ごとに吐くので、ローカルディスクなら
/// フレームに響かない。書き込みが失敗したらそのトライは記録をやめる (毎イベント例外を出し続けないため)。
/// </remarks>
internal unsafe class ScriptEventRecorder : SplatoonScript<ScriptEventRecorder.Config>
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

    #endregion

    #region class
    /********************************************************************/
    /* class                                                            */
    /********************************************************************/
    public class Config
    {
        public string LogDirectory = "";
        public float CloseGraceSeconds = 5f;   // 戦闘終了から何秒後にファイルを閉じるか

        // 何を書くか。既定は ScriptEventLogger と同じ + ディレクタ更新とパケット詠唱
        public bool LogSetupEnableDisable = false;
        public bool LogCombatStartEnd = true;
        public bool LogPhaseChange = true;
        public bool LogMapEffect = true;
        public bool LogObjectEffect = false;
        public bool LogTetherCreate = true;
        public bool LogTetherRemoval = true;
        public bool LogVFXSpawn = true;
        public bool VFXSkipPlayers = false;
        public bool VFXSkipEnemies = false;
        public bool LogStartingCastPacket = true;
        public bool LogStartingCastMemory = true;
        public bool LogMessage = false;
        public bool LogDirectorUpdate = true;
        public bool LogObjectCreation = false;
        public bool LogActorControl = false;
        public bool LogActionEffectEvent = true;
        public bool LogGainBuff = false;
        public bool LogRemoveBuff = false;
        public bool LogUpdateBuff = false;
        public bool BuffSkipPlayers = false;
        public bool BuffSkipEnemies = false;
        public bool LogReset = true;
    }

    #endregion

    #region public properties
    /********************************************************************/
    /* public properties                                                */
    /********************************************************************/
    public override HashSet<uint>? ValidTerritories { get; } = null;   // どこでも
    public override Metadata Metadata => new(1, "Redmoon");

    #endregion

    #region private properties
    /********************************************************************/
    /* private properties                                               */
    /********************************************************************/
    // ---- 開いているトライ 1 本ぶん。EndTry で戻す -----------------------------
    private StreamWriter? _writer;
    private string _fileName = "";
    private readonly Stopwatch _clock = new();
    private long _lines;
    private long _nextFlushMs;
    private long _closeAtMs;              // 0 = 閉じる予定なし。戦闘終了で猶予ぶん先を入れる
    private uint _tryTerritory;

    // ---- トライをまたいで残す -----------------------------------------------
    private int _tryNumber;               // 同じ地域での通し番号。地域が変わったら 0 に戻す
    private uint _lastTerritory;
    private string _status = "待機中";
    private CheckLevel _statusLevel = CheckLevel.Ok;

    // ---- パスのチェック。文字列が変わったときだけやり直す ---------------------
    private string? _checkedPath;
    private PathCheck? _check;
    private bool _writeTestDone;          // 今の _check に書き込み試験の結果が入っているか

    private bool Recording => _writer != null;

    #endregion

    #region public methods
    /********************************************************************/
    /* public methods                                                   */
    /********************************************************************/
    // どの override も「書くかどうか決めて、1 行組んで Write に渡す」だけ。
    // 開いていなければ Write が捨てるので、ここではトライ中かどうかを気にしない。

    public override void OnSetup() { if (C.LogSetupEnableDisable) Write("OnSetup"); }
    public override void OnEnable() { if (C.LogSetupEnableDisable) Write("OnEnable"); }

    public override void OnDisable()
    {
        if (C.LogSetupEnableDisable) Write("OnDisable");
        EndTry("スクリプト無効化");
    }

    public override void OnCombatStart() => BeginTry();

    public override void OnCombatEnd()
    {
        if (C.LogCombatStartEnd) Write("OnCombatEnd");
        if (Recording) _closeAtMs = Environment.TickCount64 + (long)(C.CloseGraceSeconds * 1000f);
    }

    public override void OnReset() { if (C.LogReset) Write("OnReset"); }

    public override void OnUpdate()
    {
        if (!Recording) return;
        var now = Environment.TickCount64;
        if (_closeAtMs != 0 && now >= _closeAtMs) { EndTry("戦闘終了"); return; }
        if (Svc.ClientState.TerritoryType != _tryTerritory) { EndTry("地域が変わった"); return; }
        if (now < _nextFlushMs) return;
        _nextFlushMs = now + 1000;
        Guard(() => _writer!.Flush());
    }

    public override void OnPhaseChange(int newPhase) { if (C.LogPhaseChange) Write($"OnPhaseChange: {newPhase}"); }

    public override void OnMapEffect(uint position, ushort data1, ushort data2)
    {
        if (C.LogMapEffect) Write($"OnMapEffect: pos={position} data1={data1} data2={data2}");
    }

    public override void OnObjectEffect(uint target, uint entityId, uint actionId)
    {
        if (!C.LogObjectEffect || target.GetObject() is not { BaseId: not 0 } obj) return;
        Write($"OnObjectEffect: {Obj(obj)} entityId={entityId} actionId={actionId}");
    }

    public override void OnTetherCreate(uint source, uint target, uint data2, uint data3, uint data5)
    {
        if (!C.LogTetherCreate) return;
        Write($"OnTetherCreate: data2={data2} data3={data3} data5={data5} " +
              $"source={Obj(source.GetObject(), source)} target={Obj(target.GetObject(), target)}");
    }

    public override void OnTetherRemoval(uint source, uint data2, uint data3, uint data5)
    {
        if (!C.LogTetherRemoval) return;
        Write($"OnTetherRemoval: data2={data2} data3={data3} data5={data5} source={Obj(source.GetObject(), source)}");
    }

    public override void OnVFXSpawn(uint target, string vfxPath)
    {
        if (!C.LogVFXSpawn) return;
        var obj = target.GetObject();
        if (IsNoiseNpc(obj)) return;
        if (C.VFXSkipPlayers && obj is IPlayerCharacter) return;
        if (C.VFXSkipEnemies && obj is IBattleNpc) return;
        // プレイヤーの共通エフェクト (スキル演出など) は量が多くギミックと無関係なので捨てる
        if (obj is IPlayerCharacter && vfxPath.Contains("vfx/common/eff/")) return;
        Write($"OnVFXSpawn: {vfxPath} target={Obj(obj, target)}");
    }

    public override void OnStartingCast(uint sourceId, PacketActorCast* packet)
    {
        if (!C.LogStartingCastPacket || !IsCastSource(sourceId.GetObject(), out var npc)) return;
        Write($"OnStartingCast[pkt]: {ActionName(packet->ActionID)} type={packet->ActionType} " +
              $"castTime={packet->CastTime:F2} target=0x{packet->TargetID:X8} " +
              $"pktP={Vec(packet->Position)} pktR={packet->Rotation:+0.000;-0.000} source={Obj(npc)}");
    }

    public override void OnStartingCast(uint source, uint castId)
    {
        if (!C.LogStartingCastMemory || !IsCastSource(source.GetObject(), out var npc)) return;
        Write($"OnStartingCast[mem]: {ActionName(castId)} source={Obj(npc)}");
    }

    public override void OnMessage(string Message) { if (C.LogMessage) Write($"OnMessage: {Message}"); }

    public override void OnDirectorUpdate(DirectorUpdateCategory category)
    {
        if (C.LogDirectorUpdate) Write($"OnDirectorUpdate: {category}");
    }

    public override void OnObjectCreation(nint newObjectPtr)
    {
        if (!C.LogObjectCreation || newObjectPtr == 0) return;
        // 生成直後は中身が埋まっていないので 1 tick 待ってから引く (ScriptEventLogger と同じ)
        _ = new TickScheduler(() =>
        {
            var obj = Svc.Objects.FirstOrDefault(o => o.Address == newObjectPtr);
            Write(obj == null ? $"OnObjectCreation: 0x{newObjectPtr:X} (見つからない)" : $"OnObjectCreation: {Obj(obj)}");
        });
    }

    public override void OnActorControl(uint sourceId, uint command, uint p1, uint p2, uint p3, uint p4,
        uint p5, uint p6, uint p7, uint p8, ulong targetId, byte replaying)
    {
        if (!C.LogActorControl) return;
        Write($"OnActorControl: cmd={command} p=[{p1},{p2},{p3},{p4},{p5},{p6},{p7},{p8}] " +
              $"target=0x{targetId:X} replaying={replaying} source={Obj(sourceId.GetObject(), sourceId)}");
    }

    public override void OnActionEffectEvent(ActionEffectSet set)
    {
        if (!C.LogActionEffectEvent || set.Action is not { } action) return;
        if (!IsCastSource(set.Source, out var npc)) return;
        Write($"OnActionEffectEvent: {action.Name}({action.RowId}) pos={Vec(set.Position)} " +
              $"source={Obj(npc)} target={Obj(set.Target)}");
    }

    public override void OnGainBuffEffect(uint sourceId, FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        if (C.LogGainBuff) WriteBuff("OnGainBuffEffect", sourceId, status);
    }

    public override void OnRemoveBuffEffect(uint sourceId, FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        if (C.LogRemoveBuff) WriteBuff("OnRemoveBuffEffect", sourceId, status);
    }

    public override void OnUpdateBuffEffect(uint sourceId, FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        if (C.LogUpdateBuff) WriteBuff("OnUpdateBuffEffect", sourceId, status);
    }

    public override void OnSettingsDraw()
    {
        DrawDirectory();
        ImGui.Separator();
        DrawStatus();
        if (ImGuiEx.CollapsingHeader("Events")) DrawFilters();
    }

    #endregion

    #region private methods
    /********************************************************************/
    /* private methods                                                  */
    /********************************************************************/

    // ---- トライの開始と終了 --------------------------------------------------

    /// <summary>新しいトライのファイルを開いて、ヘッダを書く。</summary>
    /// <remarks>パスが使えなければ開かない。黙って別の場所に書いたりはしない。
    /// 開く直前に書き込み試験までやり直す。設定したあとでフォルダを消された・USB を抜かれた、
    /// といった変化をトライの頭で拾うため。</remarks>
    private void BeginTry()
    {
        EndTry("次のトライが始まった");

        var territory = Svc.ClientState.TerritoryType;
        if (territory != _lastTerritory) { _tryNumber = 0; _lastTerritory = territory; }
        _tryNumber++;

        var check = RefreshCheck(C.LogDirectory, writeTest: true);
        if (!check.Usable)
        {
            SetStatus(CheckLevel.Error, $"トライ {_tryNumber} は記録しない: {check.Messages.First(m => m.Level == CheckLevel.Error).Text}");
            return;
        }

        var replay = Svc.Condition[ConditionFlag.DutyRecorderPlayback];
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}_T{territory}_{SafeFileName(ContentName(territory))}" +
                   $"_try{_tryNumber:D2}{(replay ? "_replay" : "")}.log";
        var path = Path.Combine(check.FullPath, name);

        Guard(() =>
        {
            // CreateNew: 同名があれば上書きせずに失敗させる。FileShare.Read: 書いている最中に開いて読めるように
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
            _writer = new StreamWriter(stream, new UTF8Encoding(false), 65536);
        });
        if (!Recording) return;

        _fileName = name;
        _tryTerritory = territory;
        _lines = 0;
        _closeAtMs = 0;
        _nextFlushMs = Environment.TickCount64 + 1000;
        _clock.Restart();
        WriteHeader(territory, replay);
        if (C.LogCombatStartEnd) Write("OnCombatStart");
        SetStatus(CheckLevel.Ok, $"記録中: {name}");
    }

    /// <summary>ファイルを閉じる。開いていなければ何もしない。どこから呼ばれてもよい。</summary>
    private void EndTry(string reason)
    {
        if (!Recording) return;
        Write($"--- 記録終了 ({reason}) / {_lines + 1} 行");
        // 直前の Write が失敗していれば、Guard がもう閉じて _writer を null にしている
        if (_writer is { } writer) Guard(() => writer.Dispose());
        _writer = null;
        _clock.Stop();
        _closeAtMs = 0;
        SetStatus(CheckLevel.Ok, $"保存した: {_fileName} ({reason})");
    }

    /// <summary>ファイルの頭に、読むのに要る前提を書く。</summary>
    /// <remarks>ログの EID は数字でしかないので、誰が誰かをここで対応させておく。
    /// 角度の規約もここに書く。後から読む人 (自分を含む) が R を方位と取り違えないように。</remarks>
    private void WriteHeader(uint territory, bool replay)
    {
        WriteRaw($"# ScriptEventRecorder v{Metadata.Version}");
        WriteRaw($"# 開始     : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        WriteRaw($"# 地域     : {territory} {ContentName(territory)}");
        WriteRaw($"# トライ   : {_tryNumber}{(replay ? " (duty recorder 再生中)" : "")}");
        WriteRaw($"# 自分     : {Obj(BasePlayer)} {Job(BasePlayer)}");
        foreach (var pc in Controller.GetPartyMembers())
            WriteRaw($"# パーティ : {Obj(pc)} {Job(pc)}");
        WriteRaw("# 時刻     : 行頭はトライ開始からの経過 (分:秒.ミリ秒)");
        WriteRaw("# R        : IGameObject.Rotation (ラジアン)。0=南 / +π/2=東 / ±π=北 / -π/2=西。方位ではない");
        WriteRaw("# 詠唱     : [pkt]=パケット (サーバ値) / [mem]=メモリ監視 (次フレームの補間値)");
        WriteRaw("");
    }

    // ---- 書き込み --------------------------------------------------------------

    /// <summary>経過時間を付けて 1 行書く。記録中でなければ捨てる。</summary>
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

    private void WriteBuff(string kind, uint targetId, FFXIVClientStructs.FFXIV.Client.Game.Status status)
    {
        if (!targetId.TryGetObject(out var obj)) return;
        if (C.BuffSkipPlayers && obj is IPlayerCharacter) return;
        if (C.BuffSkipEnemies && obj is IBattleNpc) return;
        var name = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>().GetRowOrDefault(status.StatusId)?.Name.ToString();
        // Param は「スタック数」だけでなく、ギミックの種別を載せていることがある (真偽の判定に使われる例あり)
        Write($"{kind}: {name ?? "?"}({status.StatusId}) param={status.Param} remain={status.RemainingTime:F2} " +
              $"from=0x{status.SourceObject.ObjectId:X8} on={Obj(obj)}");
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

    // ---- パスのチェック --------------------------------------------------------

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

    /// <summary>オブジェクトを 1 語で出す。null なら ID だけ出す。</summary>
    private static string Obj(IGameObject? obj, uint fallbackId = 0)
    {
        if (obj == null) return fallbackId == 0 ? "-" : $"?[EID:0x{fallbackId:X8}]";
        return $"{obj.Name}[DID:0x{obj.BaseId:X} EID:0x{obj.EntityId:X8} " +
               $"P={Vec(obj.Position)} R={obj.Rotation:+0.000;-0.000}]";
    }

    private static string Vec(System.Numerics.Vector3 v) => $"({v.X:F2},{v.Y:F2},{v.Z:F2})";

    private static string Job(IGameObject? obj) =>
        obj is IPlayerCharacter pc ? pc.ClassJob.ValueNullable?.Abbreviation.ToString() ?? "?" : "";

    private static string ActionName(uint id) =>
        $"{Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(id)?.Name.ToString() ?? "?"}({id})";

    /// <summary>ペット・チョコボ・種別なしの NPC か。ギミックと無関係なので捨てる (ScriptEventLogger と同じ)。</summary>
    private static bool IsNoiseNpc(IGameObject? obj) =>
        obj is IBattleNpc npc && npc.BattleNpcKind is BattleNpcSubKind.Pet or BattleNpcSubKind.None or BattleNpcSubKind.RaceChocobo;

    /// <summary>詠唱・着弾の出どころとして記録に値する敵か。</summary>
    private static bool IsCastSource(IGameObject? obj, out IBattleNpc npc)
    {
        npc = null!;
        if (obj is not IBattleNpc battle || battle.BaseId == 0 || IsNoiseNpc(battle)) return false;
        npc = battle;
        return true;
    }

    private static string ContentName(uint territory)
    {
        var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territory);
        var content = row?.ContentFinderCondition.ValueNullable?.Name.ToString();
        return string.IsNullOrWhiteSpace(content) ? row?.PlaceName.ValueNullable?.Name.ToString() ?? "" : content;
    }

    /// <summary>ファイル名に使えない文字と空白を _ にし、長さを抑える。</summary>
    private static string SafeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
        return cleaned.Length > 60 ? cleaned[..60] : cleaned.Length == 0 ? "unknown" : cleaned;
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

    /// <summary>今どうなっているか。</summary>
    private void DrawStatus()
    {
        ImGuiEx.Text(Color(_statusLevel), _status);
        if (Recording) ImGuiEx.Text($"経過 {_clock.Elapsed:mm\\:ss} / {_lines} 行");
    }

    /// <summary>何を書くか。ScriptEventLogger と同じ並び。</summary>
    private void DrawFilters()
    {
        ImGui.Checkbox("OnSetup / OnEnable / OnDisable", ref C.LogSetupEnableDisable);
        ImGui.Checkbox("OnCombatStart / OnCombatEnd", ref C.LogCombatStartEnd);
        ImGui.Checkbox("OnPhaseChange", ref C.LogPhaseChange);
        ImGui.Checkbox("OnMapEffect", ref C.LogMapEffect);
        ImGui.Checkbox("OnObjectEffect", ref C.LogObjectEffect);
        ImGui.Checkbox("OnTetherCreate", ref C.LogTetherCreate);
        ImGui.Checkbox("OnTetherRemoval", ref C.LogTetherRemoval);
        ImGui.Checkbox("OnVFXSpawn", ref C.LogVFXSpawn);
        ImGui.Indent();
        ImGui.Checkbox("Skip players##vfx", ref C.VFXSkipPlayers);
        ImGui.Checkbox("Skip enemies##vfx", ref C.VFXSkipEnemies);
        ImGui.Unindent();
        ImGui.Checkbox("OnStartingCast [pkt] (packet, server value)", ref C.LogStartingCastPacket);
        ImGui.Checkbox("OnStartingCast [mem] (memory poll, next frame)", ref C.LogStartingCastMemory);
        ImGui.Checkbox("OnMessage", ref C.LogMessage);
        ImGui.Checkbox("OnDirectorUpdate", ref C.LogDirectorUpdate);
        ImGui.Checkbox("OnObjectCreation", ref C.LogObjectCreation);
        ImGui.Checkbox("OnActorControl", ref C.LogActorControl);
        ImGui.Checkbox("OnActionEffectEvent", ref C.LogActionEffectEvent);
        ImGui.Checkbox("OnGainBuffEffect", ref C.LogGainBuff);
        ImGui.Checkbox("OnRemoveBuffEffect", ref C.LogRemoveBuff);
        ImGui.Checkbox("OnUpdateBuffEffect", ref C.LogUpdateBuff);
        ImGui.Indent();
        ImGui.Checkbox("Skip players##buff", ref C.BuffSkipPlayers);
        ImGui.Checkbox("Skip enemies##buff", ref C.BuffSkipEnemies);
        ImGui.Unindent();
        ImGui.Checkbox("OnReset", ref C.LogReset);
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
