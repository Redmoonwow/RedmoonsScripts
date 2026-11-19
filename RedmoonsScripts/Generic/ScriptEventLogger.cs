using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.Hooks;
using ECommons.Hooks.ActionEffectTypes;
using ECommons.Logging;
using ECommons.Schedulers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Splatoon.SplatoonScripting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Action = Lumina.Excel.Sheets.Action;

namespace RedmoonsScripts.Generic;
internal unsafe class ScriptEventLogger :SplatoonScript
{
    public override HashSet<uint>? ValidTerritories { get; } = null;
    public override Metadata Metadata => new(16, "Redmoon");

    private Config Conf => Controller.GetConfig<Config>();

    // ---- 出力先 ----
    // dalamud.log には書かない。人間用 = Splatoon のログ窓 (InternalLog、最新 1000 行) + ScriptEventLogger.log、
    // AI 用 = ScriptEventLogger.ai.log (オプション)。どちらも 100MB を超えたら .old に回して作り直す (各 最大 約 200MB)。
    // dalamud.log は 100MB で書けなくなり、それ以降のログ (このスクリプト以外も) が全部消えていた (2026-11-19)。
    //
    // メインスレッドではイベントの値を取り出して待ち行列に積むだけ (文字列は作らない、ファイルに触らない)。
    // 別名・技/状態の名前引き・文字列の組み立て・書き込み・作り直しは専用スレッド (優先度低、0.2 秒ごと) でやる。
    // キャラの名前などゲームのオブジェクトを読むのは、そのキャラが初めて出た時だけメインで 1 回。
    // (人間用の文字列は、今まで通りメインで作る。フィルタを切れば作られない)
    private const long MaxLogBytes = 100L * 1024 * 1024;
    private const int WriteBufferBytes = 64 * 1024;

    private static string LogDir => Svc.PluginInterface.GetPluginConfigDirectory();
    private static string LogPath => Path.Combine(LogDir, "ScriptEventLogger.log");
    private static string OldLogPath => Path.Combine(LogDir, "ScriptEventLogger.old.log");
    private static string AiPath => Path.Combine(LogDir, "ScriptEventLogger.ai.log");
    private static string AiOldPath => Path.Combine(LogDir, "ScriptEventLogger.ai.old.log");

    private Worker? _worker;
    private Worker W => _worker ??= new Worker();
    private readonly HashSet<uint> _sentEntities = [];   // 定義を専用スレッドへ送ったキャラ (メインスレッドだけで触る)

    // 人間用。ログ窓はメインで (InternalLog はメインで触る)、ファイルは専用スレッドへ
    private void Log(string s)
    {
        Svc.Framework?.RunOnFrameworkThread(() => InternalLog.Information(s));
        if (!Conf.WriteToFile) return;
        var t = DateTime.Now;
        W.Post(w => w.Human(t, s));
    }

    // AI 用の 1 件。build は専用スレッドで動くので、ゲームのオブジェクトに触らない (値は呼ぶ側で取り出しておく)
    private bool AiOn => Conf.AiLog;
    private void Ai(Func<Worker, string> build)
    {
        var t = Environment.TickCount64;
        W.Post(w => w.AiEvent(t, build));
    }

    // キャラ。初めて出た時だけメインで中身を読んで送る。返すのは ID (専用スレッドで別名に変える)
    private uint Ent(ulong id)
    {
        var key = (uint)id;
        if (key == 0 || key == 0xE0000000 || !_sentEntities.Add(key)) return key;
        var o = key.GetObject();
        if (o == null) { _sentEntities.Remove(key); return key; }   // まだ居ない。次に出た時に読む (それまでは "? O ?")
        var kind = o is IPlayerCharacter ? "P" : o is IBattleNpc ? "N" : "O";
        if (BasePlayer?.EntityId == key) kind += "*";
        var name = o.Name.ToString().Replace(' ', '_');
        if (o is IPlayerCharacter pc) name += "/" + pc.ClassJob.Value.Abbreviation;
        var def = $"{key:X} {o.BaseId} {kind} {name} @{o.Position.X:0.#},{o.Position.Z:0.#},{o.Rotation:0.##}";
        W.Post(w => w.Define(key, def));
        return key;
    }

    // 座標 (メインで取る。文字列にするのは専用スレッド)
    private readonly record struct Pt(bool Ok, float X, float Z, float R);
    private static Pt Grab(IGameObject? o) => o == null ? default : new(true, o.Position.X, o.Position.Z, o.Rotation);

    // ---- 専用スレッド ----
    private sealed class Worker
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<System.Action<Worker>> _q = new();
        private readonly System.Threading.Thread _thread;
        private volatile bool _stop;
        public volatile bool AiEnabled, HumanEnabled = true;
        public volatile uint Zone;   // ファイルの見出し用。メインで OnUpdate が入れる
        private Writer? _human, _ai;
        private long _lastAiMs, _curT;
        private readonly Dictionary<uint, string> _defs = [];     // キャラの定義 (ファイルを作り直しても残す)
        private readonly Dictionary<uint, string> _alias = [];    // 今の AI ファイルの別名
        private readonly Dictionary<string, string> _vfx = [];
        private readonly HashSet<uint> _act = [], _sta = [];
        public long HumanBytes => _human?.Bytes ?? 0;
        public long AiBytes => _ai?.Bytes ?? 0;

        private const string Legend =
            "#legend 1行=<dt> <code> ... dt=前の行からのms(イベントの時刻)。#AI1 で別名表を初期化。名前の空白は_。座標 @x,z,向きrad\n" +
            "#def =<e> <entityIdHex> <baseId> <P|N|O>[*=自分] <名前|名前/ジョブ> @pos : キャラの別名(36進、同じ別名がまた出たら上書き。「? O ?」 はまだ居なかったキャラ) / =a<id> <名前>:技 / =s<id> <名前>:状態 / =v<n> <path>:VFX\n" +
            "#ev C src act castMs @pos:詠唱開始 | E src act 主tgt 対象数 @pos:技の効果 | A act src tgt 印 効果量 hp0>hp1 [sh0>sh1] +遅延ms [r=受け手]:結果(印 .=なし b=Basic s=to_source n=AEなし / 効果 D=dmg B=blocked P=parried H=heal -=なし)\n" +
            "#ev V tgt v:VFX(全部) | B+ tgt sta param 残秒 src / B- tgt sta / B~ tgt sta param 残秒:状態 | T src tgt d2 d3 d5 / t src d2 d3 d5:線 | M pos d1 d2 | G tgt eid act:ObjectEffect\n" +
            "#ev K src cmd p1..p8(末尾の0は省略) tgt rep:ActorControl | D cat(16進):Director | P n:フェーズ | !S/!E/!R:戦闘開始(#T=unixms)/終了/リセット | X 文:メッセージ | N e:オブジェクト生成\n" +
            "#ev Dx tgt cat a4..a9:Director(全引数) | F act anim type src tgt dmg:効果1つずつ(type=ActionEffectType の数値) | !U/!+/!-:スクリプトの Setup/Enable/Disable";

        // 最後に作った書き手のスレッド。スクリプトを読み直すと型もフィールドも別物になるので、AppDomain に置いて引き継ぐ
        private const string LastThreadKey = "RedmoonsScripts.ScriptEventLogger.WriterThread";
        private readonly System.Threading.ManualResetEventSlim _wake = new(false);

        public Worker()
        {
            var prev = AppDomain.CurrentDomain.GetData(LastThreadKey) as System.Threading.Thread;
            _thread = new System.Threading.Thread(() => Run(prev)) { IsBackground = true, Name = "ScriptEventLogger", Priority = System.Threading.ThreadPriority.BelowNormal };
            AppDomain.CurrentDomain.SetData(LastThreadKey, _thread);
            _thread.Start();
        }

        public void Post(System.Action<Worker> a) => _q.Enqueue(a);

        private void Run(System.Threading.Thread? prev)
        {
            // 前の書き手 (無効にした直後・読み直す前のスクリプト) が書き終えるまでファイルに触らない (同じファイルに 2 本で書くと行が混ざる)。
            // 待っている間に来た分は待ち行列に溜まるだけで、落ちない
            if (prev != null && prev != System.Threading.Thread.CurrentThread) prev.Join(10000);
            while (!_stop)
            {
                _wake.Wait(200);
                Drain();
            }
            Drain();
            _human?.Dispose();
            _ai?.Dispose();
        }

        private void Drain()
        {
            var any = false;
            while (_q.TryDequeue(out var a))
            {
                any = true;
                try { a(this); }
                catch (Exception e) { Svc.Framework?.RunOnFrameworkThread(() => InternalLog.Error($"ScriptEventLogger: 書き出しで例外 ({e.Message})")); }
            }
            if (any) { _human?.Flush(); _ai?.Flush(); }
            if (!AiEnabled && _ai != null) { _ai.Dispose(); _ai = null; }
            if (!HumanEnabled && _human != null) { _human.Dispose(); _human = null; }
        }

        // 止める。スレッドは残りを全部書いてファイルを閉じてから終わる (途中で切らない)。
        // wait=false ならメインは待たない。ゲーム終了の時だけ wait=true (プロセスごと消えて書き残すのを避ける)
        public void Stop(bool wait)
        {
            _stop = true;
            _wake.Set();
            if (wait) _thread.Join(10000);
        }

        public void Human(DateTime t, string s)
        {
            _human ??= new Writer(LogPath, OldLogPath, w => w.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ---- ScriptEventLogger.log を開いた (100MB で .old.log に回す) ----"));
            _human.Line($"{t:yyyy-MM-dd HH:mm:ss.fff} {s}");
            _human.RotateIfFull();
        }

        // 別名がもう付いていれば、同じ別名で定義し直す (後から居るようになった / リセット後に ID が使い回された)
        public void Define(uint id, string def)
        {
            _defs[id] = def;
            if (_ai != null && _alias.TryGetValue(id, out var a)) AiRaw($"={a} {def}");
        }

        public void AiEvent(long t, Func<Worker, string> build)
        {
            _curT = t;
            _ai ??= new Writer(AiPath, AiOldPath, OnAiOpen);
            _ai.RotateIfFull();
            var body = build(this);   // 定義の行 (初めての別名など) はここで先に書かれる
            AiRaw(body);
        }

        private void OnAiOpen(StreamWriter w)
        {
            _alias.Clear(); _vfx.Clear(); _act.Clear(); _sta.Clear();
            _lastAiMs = _curT;
            w.WriteLine($"#AI1 t0={DateTimeOffset.Now.ToUnixTimeMilliseconds()} {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} zone={Zone}");
            w.WriteLine(Legend);
        }

        private void AiRaw(string body)
        {
            _ai!.Line($"{_curT - _lastAiMs} {body}");
            _lastAiMs = _curT;
        }

        public string E(uint id)
        {
            if (id == 0 || id == 0xE0000000) return "-";
            if (_alias.TryGetValue(id, out var a)) return a;
            a = B36(_alias.Count);
            _alias[id] = a;
            AiRaw($"={a} {(_defs.TryGetValue(id, out var d) ? d : $"{id:X} ? O ?")}");
            return a;
        }

        public string Act(uint id)
        {
            if (id != 0 && _act.Add(id)) AiRaw($"=a{id} {(Svc.Data.GetExcelSheet<Action>()!.GetRowOrDefault(id)?.Name.ToString() ?? "?").Replace(' ', '_')}");
            return id.ToString();
        }

        public string Sta(uint id)
        {
            if (id != 0 && _sta.Add(id)) AiRaw($"=s{id} {(Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>()!.GetRowOrDefault(id)?.Name.ToString() ?? "?").Replace(' ', '_')}");
            return id.ToString();
        }

        public string Vfx(string path)
        {
            if (_vfx.TryGetValue(path, out var v)) return v;
            v = "v" + B36(_vfx.Count);
            _vfx[path] = v;
            AiRaw($"={v} {path}");
            return v;
        }

        public static string Pos(Pt p) => p.Ok ? $"@{p.X:0.#},{p.Z:0.#},{p.R:0.##}" : "@-";

        private static string B36(int n)
        {
            const string d = "0123456789abcdefghijklmnopqrstuvwxyz";
            if (n == 0) return "0";
            var sb = new StringBuilder();
            while (n > 0) { sb.Insert(0, d[n % 36]); n /= 36; }
            return sb.ToString();
        }
    }

    // 1 ファイル。100MB を超えたら .old に回して作り直す。開くたびに onOpen (見出し) を書く
    private sealed class Writer(string path, string oldPath, System.Action<StreamWriter> onOpen) : IDisposable
    {
        private StreamWriter? _w;
        public long Bytes { get; private set; }

        private StreamWriter Open()
        {
            if (_w != null) return _w;
            if (File.Exists(path) && new FileInfo(path).Length >= MaxLogBytes) File.Move(path, oldPath, overwrite: true);
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, WriteBufferBytes);
            Bytes = fs.Length;
            _w = new StreamWriter(fs, new UTF8Encoding(false), WriteBufferBytes) { AutoFlush = false };
            onOpen(_w);
            return _w;
        }

        public void Line(string line)
        {
            Open().WriteLine(line);
            Bytes += Encoding.UTF8.GetByteCount(line) + 2;
        }

        // 作り直しはイベントの切れ目でだけ (1 件の途中で別名表が消えないように)
        public void RotateIfFull()
        {
            if (Bytes < MaxLogBytes) return;
            Dispose();
            File.Move(path, oldPath, overwrite: true);
            Bytes = 0;
        }

        public void Flush() => _w?.Flush();

        public void Dispose()
        {
            try { _w?.Flush(); _w?.Dispose(); } catch { }
            _w = null;
        }
    }

    // 専用スレッドに設定を伝えるだけ (軽い)
    public override void OnUpdate()
    {
        if (_worker == null) return;
        _worker.AiEnabled = Conf.AiLog;
        _worker.HumanEnabled = Conf.WriteToFile;
        _worker.Zone = Svc.ClientState.TerritoryType;
    }

    public override void OnSetup()
    {
        if (AiOn) Ai(_ => "!U");
        if (!Conf.FilterOnSetup) return;
        Log("OnSetup Called");
    }

    public override void OnEnable()
    {
        if (AiOn) Ai(_ => "!+");
        if (!Conf.FilterOnEnable)
            return;
        Log("OnEnable Called");
    }

    public override void OnDisable()
    {
        if (Conf.FilterOnDisable) Log("OnDisable Called");
        if (AiOn) Ai(_ => "!-");
        // 無効にした時・読み直した時は専用スレッドを止める。積んであった分は全部書いてから閉じる (メインは待たない)。
        // 次の書き手は前のが書き終えるのを待ってから書き始める。ゲーム終了の時だけはメインで書き終わりを待つ
        _worker?.Stop(wait: Svc.Framework?.IsFrameworkUnloading ?? true);
        _worker = null;
        _sentEntities.Clear();
    }

    public override void OnCombatStart()
    {
        if (AiOn) { var u = DateTimeOffset.Now.ToUnixTimeMilliseconds(); Ai(_ => $"!S #T {u}"); }
        if (!Conf.FilterOnCombatStart)
            return;
        Log("OnCombatStart Called");
    }

    public override void OnCombatEnd()
    {
        if (AiOn) Ai(_ => "!E");
        if (!Conf.FilterOnCombatEnd)
            return;
        Log("OnCombatEnd Called");
    }

    public override void OnPhaseChange(int newPhase)
    {
        if (AiOn) Ai(_ => $"P {newPhase}");
        if (!Conf.FilterOnPhaseChange)
            return;
        Log($"OnPhaseChange: {newPhase}");
    }

    public override void OnMapEffect(uint position, ushort data1, ushort data2)
    {
        if (AiOn) Ai(_ => $"M {position} {data1} {data2}");
        if (!Conf.FilterOnMapEffect)
            return;
        Log($"OnMapEffect: {position} - {data1} - {data2}");
    }

    public override void OnObjectEffect(uint target, uint entityId, uint actionId)
    {
        if (AiOn) { var t = Ent(target); Ai(w => $"G {w.E(t)} {entityId} {actionId}"); }
        if (!Conf.FilterOnObjectEffect)
            return;
        var targetObj = target.GetObject();
        if (targetObj == null || targetObj.BaseId == 0)
            return;

        Log($"OnObjectEffect: {targetObj.Name}{targetObj.Position}(GID: {targetObj.GameObjectId} DID: {targetObj.BaseId}) - {entityId} - {actionId}");
    }

    public override void OnTetherCreate(uint source, uint target, uint data2, uint data3, uint data5)
    {
        if (AiOn) { var a = Ent(source); var b = Ent(target); Ai(w => $"T {w.E(a)} {w.E(b)} {data2} {data3} {data5}"); }
        if (!Conf.FilterOnTetherCreate)
            return;

        var sourceObj = source.GetObject();
        var targetObj = target.GetObject();
        if (sourceObj != null && targetObj != null)
        {
            Log($"OnTetherCreate: - {data2} - {data3} - {data5}");
            Log($"    Source: {sourceObj.Name}{sourceObj.Position}(GID: {sourceObj.GameObjectId} DID: {sourceObj.BaseId})");
            Log($"    Target: {targetObj.Name}{targetObj.Position}(GID: {targetObj.GameObjectId} DID: {targetObj.BaseId})");
        }
        else if (sourceObj != null && targetObj == null)
        {
            Log($"OnTetherCreate: - {data2} - {data3} - {data5}");
            Log($"    Source: {sourceObj.Name}{sourceObj.Position}(GID: {sourceObj.GameObjectId} DID: {sourceObj.BaseId})");
        }
        else if (sourceObj == null && targetObj != null)
        {
            Log($"OnTetherCreate: - {data2} - {data3} - {data5}");
            Log($"    Target: {targetObj.Name}{targetObj.Position}(GID: {targetObj.GameObjectId} DID: {targetObj.BaseId})");
        }
        else
        {
            Log($"OnTetherCreate: - {data2} - {data3} - {data5}");
        }
    }

    public override void OnTetherRemoval(uint source, uint data2, uint data3, uint data5)
    {
        if (AiOn) { var a = Ent(source); Ai(w => $"t {w.E(a)} {data2} {data3} {data5}"); }
        if (!Conf.FilterOnTetherRemoval)
            return;

        var sourceObj = source.GetObject();

        if (sourceObj == null)
        {
            Log($"OnTetherRemoval: - {data2} - {data3} - {data5}");
        }
        else
        {
            Log($"OnTetherRemoval: - {data2} - {data3} - {data5}");
            Log($"    Source: {sourceObj.Name}{sourceObj.Position}(GID: {sourceObj.GameObjectId} DID: {sourceObj.BaseId})");
        }
    }
    public override void OnVFXSpawn(uint target, string vfxPath)
    {
        if (AiOn) { var t = Ent(target); Ai(w => $"V {w.E(t)} {w.Vfx(vfxPath)}"); }
        if (!Conf.FilterOnVFXSpawn) return;
        var targetObj = target.GetObject();

        if (target.GetObject() is IBattleNpc npc && (npc.BattleNpcKind == BattleNpcSubKind.Pet ||
            npc.BattleNpcKind == BattleNpcSubKind.None || npc.BattleNpcKind == BattleNpcSubKind.RaceChocobo)) return;

        if (Conf.FilterOnVFXSpawnSubFilterPlayers && targetObj is IPlayerCharacter) return;
        if (Conf.FilterOnVFXSpawnSubFilterEnemies && targetObj is IBattleNpc) return;

        if (targetObj is IPlayerCharacter && vfxPath.Contains("vfx/common/eff/")) return;

        if (targetObj == null)
            Log($"OnVFXSpawn: {vfxPath}");
        else
            Log($"OnVFXSpawn: {vfxPath} - {targetObj.Name}{targetObj.Position}(GID: {targetObj.GameObjectId} DID: {targetObj.BaseId})");
    }

    public override void OnStartingCast(uint source, uint castId)
    {
        if (AiOn)
        {
            var so = source.GetObject();
            var castMs = so is IBattleChara bc && bc.IsCasting ? (int)(bc.TotalCastTime * 1000) : 0;
            var pt = Grab(so);
            var a = Ent(source);
            Ai(w => $"C {w.E(a)} {w.Act(castId)} {castMs} {Worker.Pos(pt)}");
        }
        if (!Conf.FilterOnStartingCast) return;
        if (source.GetObject() is not IBattleNpc npc ||
            npc.BattleNpcKind == BattleNpcSubKind.Pet ||
            npc.BattleNpcKind == BattleNpcSubKind.None ||
            npc.BattleNpcKind == BattleNpcSubKind.RaceChocobo) return;
        if (npc == null || npc.BaseId == 0) return;

        var action = Svc.Data.GetExcelSheet<Action>()!.GetRowOrDefault(castId);

        if (action.HasValue)
        {
            Log($"OnStartingCast: Cast: {action.Value.Name}({castId}) - source: {npc.Name}{npc.Position}(GID: 0x{npc.GameObjectId.ToString("X8")} DID: {npc.BaseId})");
        }
        else
        {
            Log($"OnStartingCast: Cast: {castId} - source: {npc.Name}{npc.Position}(GID: 0x{npc.GameObjectId.ToString("X8")} DID: {npc.BaseId})");
        }
    }

    public override void OnMessage(string Message)
    {
        if (AiOn) Ai(_ => $"X {Message.Replace('\n', ' ')}");
        if (!Conf.FilterOnMessage) return;
        Log($"OnMessage: {Message}");
    }

    public override void OnDirectorUpdate(DirectorUpdateCategory category)
    {
        if (AiOn) Ai(_ => $"D {(uint)category:X}");
        if (!Conf.FilterOnDirectorUpdate) return;
        if (category == DirectorUpdateCategory.Commence)
            Log("OnDirectorUpdate: Commence");
        else if (category == DirectorUpdateCategory.Recommence)
            Log("OnDirectorUpdate: Recommence");
        else if (category == DirectorUpdateCategory.Complete)
            Log("OnDirectorUpdate: Complete");
        else if (category == DirectorUpdateCategory.Wipe)
            Log("OnDirectorUpdate: Wipe");
    }

    // AI 用ログだけ: 引数の多い方の Director と、効果 1 つずつの ActionEffect
    public override void OnDirectorUpdate(nint directorPtr, uint targetId, DirectorUpdateCategory a3, uint a4, uint a5, int a6, int a7, int a8, int a9)
    {
        if (AiOn) { var t = Ent(targetId); Ai(w => $"Dx {w.E(t)} {(uint)a3:X} {a4} {a5} {a6} {a7} {a8} {a9}"); }
    }

    public override void OnActionEffect(uint ActionID, ushort animationID, ActionEffectType type, uint sourceID, ulong targetOID, uint damage)
    {
        if (AiOn) { var a = Ent(sourceID); var b = Ent(targetOID); Ai(w => $"F {w.Act(ActionID)} {animationID} {(int)type} {w.E(a)} {w.E(b)} {damage}"); }
    }

    public override void OnObjectCreation(nint newObjectPtr)
    {
        if (AiOn && newObjectPtr == 0) Ai(_ => "N -");
        else if (AiOn)
            _ = new TickScheduler(() =>
            {
                var go = Svc.Objects.FirstOrDefault(o => o.Address == newObjectPtr);
                if (go != null) { var a = Ent(go.EntityId); Ai(w => $"N {w.E(a)}"); }
                else Ai(_ => $"N ?{newObjectPtr:X}");
            });
        if (!Conf.FilterOnObjectCreation) return;
        if (newObjectPtr == 0)
        {
            Log("OnObjectCreation: Invalid object pointer");
            return;
        }
        else
        {
            _ = new TickScheduler(() =>
            {
                var gameObject = Svc.Objects.Where(o => o.Address == newObjectPtr).FirstOrDefault();
                if (gameObject == null)
                {
                    Log($"OnObjectCreation: Object not found for pointer {newObjectPtr:X}");
                }
                else
                {
                    Log(
                        $"OnObjectCreation: {gameObject.Name.ToString()}({gameObject.Position})" +
                        $"(GID: 0x{gameObject.EntityId.ToString("X8")} DID: {gameObject.BaseId.ToString("X8")})");
                }
            }
            );
        }
    }

    public override void OnActorControl(uint sourceId, uint command, uint p1, uint p2, uint p3, uint p4, uint p5, uint p6, uint p7, uint p8, ulong targetId, byte replaying)
    {
        if (AiOn)
        {
            var a = Ent(sourceId);
            var b = Ent(targetId);
            Ai(w =>
            {
                uint[] ps = [p1, p2, p3, p4, p5, p6, p7, p8];
                var n = ps.Length;
                while (n > 0 && ps[n - 1] == 0) n--;
                return $"K {w.E(a)} {command} {string.Join(",", ps.Take(n))} {w.E(b)} {replaying}";
            });
        }
        if (!Conf.FilterOnActorControl) return;
        Log($"OnActorControl: {sourceId} - {command} - {p1} - {p2} - {p3} - {p4} - {p5} - {p6} - {p7} - {p8} - {targetId} - {replaying}");
    }

    public override void OnActionEffectEvent(ActionEffectSet set)
    {
        if (AiOn)
        {
            var a = Ent(set.Source?.EntityId ?? 0);
            var b = Ent(set.Target?.EntityId ?? 0);
            var act = set.Header.ActionID;
            var n = set.TargetEffects.Length;
            var pt = Grab(set.Source);
            Ai(w => $"E {w.E(a)} {w.Act(act)} {w.E(b)} {n} {Worker.Pos(pt)}");
        }
        if (!Conf.FilterOnActionEffectEvent) return;
        if (set.Action == null || set.Source == null || set.Source.BaseId == 0) return;

        if (set.Source is not IBattleNpc npc ||
            npc.BattleNpcKind == BattleNpcSubKind.Pet ||
            npc.BattleNpcKind == BattleNpcSubKind.None ||
            npc.BattleNpcKind == BattleNpcSubKind.RaceChocobo) return;

        if (set.Target == null)
            Log(
                $"OnActionEffectEvent: " +
                $"{set.Action.Value.Name}({set.Action.Value.RowId}) - " +
                $"Source: {set.Source.Name}{set.Source.Position}" +
                $"(GID: 0x{set.Source.GameObjectId.ToString("X8")} DID: {set.Source.BaseId.ToString("X8")})");
        else
            Log(
                $"OnActionEffectEvent: {set.Action.Value.Name}({set.Action.Value.RowId}) - " +
                $"Source: {set.Source.Name}{set.Source.Position}" +
                $"(GID: 0x{set.Source.GameObjectId.ToString("X8")} DID: {set.Source.BaseId.ToString("X8")}) - " +
                $"Target: {set.Target.Name}{set.Target.Position}" +
                $"(GID: 0x{set.Target.GameObjectId.ToString("X8")} DID: {set.Target.BaseId.ToString("X8")})");
    }

    public override void OnActionEffectApplied(ActionEffectSet set, int targetIndex, ActionEffectResult result)
    {
        if (AiOn)
        {
            var act = set.Header.ActionID;
            var r = result;   // 値の構造体 (Effects は受け取った時に写した配列で、書き換えられない)
            var sa = Ent(r.SourceId);
            var ta = Ent(r.TargetId);
            var ra = Ent(r.RecipientId);
            Ai(w => AppliedLine(w, act, r, sa, ta, ra));
        }
        if (!Conf.FilterOnActionEffectApplied) return;
        if (Conf.FilterOnActionEffectAppliedSubFilterNoActionEffect && !result.HasActionEffect) return;
        AppliedHuman(set, targetIndex, result);
    }

    // 専用スレッドで動く
    private static string AppliedLine(Worker ai, uint act, ActionEffectResult result, uint sa, uint ta, uint ra)
    {
        {
            var flags = (result.IsBasic ? "b" : "") + (result.IsSource ? "s" : "") + (result.HasActionEffect ? "" : "n");
            var ef = result.HasActionEffect
                ? result.Effects.FirstOrDefault(e => e.type is ActionEffectType.Damage or ActionEffectType.BlockedDamage or ActionEffectType.ParriedDamage or ActionEffectType.Heal)
                : default;
            var efs = ef.type switch
            {
                ActionEffectType.Damage => "D" + ef.Damage,
                ActionEffectType.BlockedDamage => "B" + ef.Damage,
                ActionEffectType.ParriedDamage => "P" + ef.Damage,
                ActionEffectType.Heal => "H" + ef.Damage,
                _ => "-",
            };
            var sh = result.ShieldAfter >= 0 && (result.ShieldBefore > 0 || result.ShieldAfter > 0) ? $" {result.ShieldBefore}>{result.ShieldAfter}" : "";
            var rcp = ra != ta ? $" r={ai.E(ra)}" : "";
            return $"A {ai.Act(act)} {ai.E(sa)} {ai.E(ta)} {(flags == "" ? "." : flags)} {efs} {result.HpBefore}>{result.HpAfter}{sh} +{result.DelayMs}{rcp}";
        }
    }

    private void AppliedHuman(ActionEffectSet set, int targetIndex, ActionEffectResult result)
    {
        var target = result.TargetId.GetObject();
        if (Conf.FilterOnActionEffectAppliedSubFilterPlayers && target is IPlayerCharacter) return;
        if (Conf.FilterOnActionEffectAppliedSubFilterEnemies && target is IBattleNpc) return;
        if (Conf.FilterOnActionEffectAppliedSubFilterSource && result.IsSource) return;

        var source = result.SourceId.GetObject();
        var recipient = result.RecipientId.GetObject();
        var shield = result.ShieldAfter >= 0 ? $" Shield: {result.ShieldBefore}% -> {result.ShieldAfter}%" : "";
        var kind = $"{(result.IsBasic ? "Basic" : "Full")}{(result.IsSource ? " to source" : "")}";
        var hp = $"Recipient: {recipient?.Name}(GID: 0x{result.RecipientId:X8}) HP: {result.HpBefore} -> {result.HpAfter}{shield}";
        if (!result.HasActionEffect)
        {
            Log(
                $"OnActionEffectApplied: (no ActionEffect, seq {result.Sequence}) - " +
                $"Target: {target?.Name}(GID: 0x{result.TargetId:X8}) - {kind} {hp}");
            return;
        }

        var effect = result.Effects.FirstOrDefault(e => e.type is ActionEffectType.Damage or ActionEffectType.BlockedDamage or ActionEffectType.ParriedDamage or ActionEffectType.Heal);
        Log(
            $"OnActionEffectApplied: {set.Name}({set.Header.ActionID}) - " +
            $"Source: {source?.Name}(GID: 0x{result.SourceId:X8}) - " +
            $"Target[{targetIndex}]: {target?.Name}(GID: 0x{result.TargetId:X8}) - " +
            $"{kind} {effect.type} {effect.Damage} {hp} (+{result.DelayMs}ms)");
    }

    public override void OnGainBuffEffect(uint sourceId, Status Status)
    {
        if (AiOn) { var a = Ent(sourceId); var b = Ent(Status.SourceObject.ObjectId); var id = Status.StatusId; var pa = Status.Param; var rt = Status.RemainingTime; Ai(w => $"B+ {w.E(a)} {w.Sta(id)} {pa} {rt:0.#} {w.E(b)}"); }
        if (!Conf.FilterOnGainBuffEffect) return;
        if (!sourceId.TryGetObject(out var gameObject)) return;
        if (gameObject == null) return;
        if (Conf.FilterOnGainBuffEffectSubFilterPlayers && gameObject is IPlayerCharacter) return;
        if (Conf.FilterOnGainBuffEffectSubFilterEnemies && gameObject is IBattleNpc) return;

        var BuffName = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>().GetRowOrDefault(Status.StatusId)?.Name.ToString();

        if (BuffName == null)
        {
            Log($"OnGainBuffEffect: [{gameObject.Name}] ({Status.StatusId})");
        }
        else
        {
            Log($"OnGainBuffEffect: [{gameObject.Name}({BuffName})] ({Status.StatusId})");
        }
    }

    public override void OnRemoveBuffEffect(uint sourceId, Status Status)
    {
        if (AiOn) { var a = Ent(sourceId); var id = Status.StatusId; Ai(w => $"B- {w.E(a)} {w.Sta(id)}"); }
        if (!Conf.FilterOnRemoveBuffEffect) return;
        if (!sourceId.TryGetObject(out var gameObject)) return;
        if (Conf.FilterOnRemoveBuffEffectSubFilterPlayers && gameObject is IPlayerCharacter) return;
        if (Conf.FilterOnRemoveBuffEffectSubFilterEnemies && gameObject is IBattleNpc) return;

        var BuffName = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>().GetRowOrDefault(Status.StatusId)?.Name.ToString();

        if (BuffName == null)
        {
            Log($"OnRemoveBuffEffect: [{gameObject.Name}({sourceId})] ({Status.StatusId})");
        }
        else
        {
            Log($"OnRemoveBuffEffect: [{gameObject.Name}({sourceId})({BuffName})] ({Status.StatusId})");
        }
    }

    public override void OnUpdateBuffEffect(uint sourceId, Status status)
    {
        if (AiOn) { var a = Ent(sourceId); var id = status.StatusId; var pa = status.Param; var rt = status.RemainingTime; Ai(w => $"B~ {w.E(a)} {w.Sta(id)} {pa} {rt:0.#}"); }
        if (!Conf.FilterOnUpdateBuffEffect) return;
        if (!sourceId.TryGetObject(out var gameObject)) return;
        if (Conf.FilterOnUpdateBuffEffectSubFilterPlayers && gameObject is IPlayerCharacter) return;
        if (Conf.FilterOnUpdateBuffEffectSubFilterEnemies && gameObject is IBattleNpc) return;

        var BuffName = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>().GetRowOrDefault(status.StatusId)?.Name.ToString();

        if (BuffName == null)
        {
            Log($"OnUpdateBuffEffect: [{gameObject.Name}({sourceId})] ({status.StatusId})");
        }
        else
        {
            Log($"OnUpdateBuffEffect: [{gameObject.Name}({sourceId})({BuffName})] ({status.StatusId})");
        }
    }


    public override void OnReset()
    {
        if (AiOn) Ai(_ => "!R");
        _sentEntities.Clear();   // ワイプ・エリア移動で ID が使い回されるので、次に出た時に読み直す
        if (!Conf.FilterOnReset)
            return;
        Log("OnReset Called");
    }

    public override void OnSettingsDraw()
    {
        ImGui.Checkbox("専用ファイルに書く (dalamud.log には書かない)##writefile", ref Conf.WriteToFile);
        var size = (_worker?.HumanBytes ?? 0) / 1024.0 / 1024.0;
        ImGui.TextUnformatted($"{LogPath} ({size:0.0} MB / 100 MB で .old.log に回す)");
        ImGui.Checkbox("AI 用ログを出す (人が読む用ではない。フィルタに関係なく全部、詰めて書く)##ailog", ref Conf.AiLog);
        var aiSize = (_worker?.AiBytes ?? 0) / 1024.0 / 1024.0;
        ImGui.TextUnformatted($"{AiPath} ({aiSize:0.0} MB / 100 MB で .ai.old.log に回す)");
        ImGui.Separator();
        ImGui.Text("Filter Events");
        if (ImGui.Button("Reset Filters")) Conf.Reset();

        ImGui.Checkbox($"OnSetup()##{nameof(Conf.FilterOnSetup)}", ref Conf.FilterOnSetup);
        ImGui.Checkbox($"OnEnable()##{nameof(Conf.FilterOnEnable)}", ref Conf.FilterOnEnable);
        ImGui.Checkbox($"OnDisable()##{nameof(Conf.FilterOnDisable)}", ref Conf.FilterOnDisable);
        ImGui.Checkbox($"OnCombatStart()##{nameof(Conf.FilterOnCombatStart)}", ref Conf.FilterOnCombatStart);
        ImGui.Checkbox($"OnCombatEnd()##{nameof(Conf.FilterOnCombatEnd)}", ref Conf.FilterOnCombatEnd);
        ImGui.Checkbox($"OnPhaseChange()##{nameof(Conf.FilterOnPhaseChange)}", ref Conf.FilterOnPhaseChange);
        ImGui.Checkbox($"OnMapEffect()##{nameof(Conf.FilterOnMapEffect)}", ref Conf.FilterOnMapEffect);
        ImGui.Checkbox($"OnObjectEffect()##{nameof(Conf.FilterOnObjectEffect)}", ref Conf.FilterOnObjectEffect);
        ImGui.Checkbox($"OnTetherCreate()##{nameof(Conf.FilterOnTetherCreate)}", ref Conf.FilterOnTetherCreate);
        ImGui.Checkbox($"OnTetherRemoval()##{nameof(Conf.FilterOnTetherRemoval)}", ref Conf.FilterOnTetherRemoval);
        ImGui.Checkbox($"OnVFXSpawn()##{nameof(Conf.FilterOnVFXSpawn)}", ref Conf.FilterOnVFXSpawn);
        ImGui.Indent();
        ImGui.Checkbox($"Filter Players##{nameof(Conf.FilterOnVFXSpawnSubFilterPlayers)}", ref Conf.FilterOnVFXSpawnSubFilterPlayers);
        ImGui.Checkbox($"Filter Enemies##{nameof(Conf.FilterOnVFXSpawnSubFilterEnemies)}", ref Conf.FilterOnVFXSpawnSubFilterEnemies);
        ImGui.Unindent();
        ImGui.Checkbox($"OnStartingCast()##{nameof(Conf.FilterOnStartingCast)}", ref Conf.FilterOnStartingCast);
        ImGui.Checkbox($"OnMessage()##{nameof(Conf.FilterOnMessage)}", ref Conf.FilterOnMessage);
        ImGui.Checkbox($"OnDirectorUpdate()##{nameof(Conf.FilterOnDirectorUpdate)}", ref Conf.FilterOnDirectorUpdate);
        ImGui.Checkbox($"OnObjectCreation()##{nameof(Conf.FilterOnObjectCreation)}", ref Conf.FilterOnObjectCreation);
        ImGui.Checkbox($"OnActorControl()##{nameof(Conf.FilterOnActorControl)}", ref Conf.FilterOnActorControl);
        ImGui.Checkbox($"OnActionEffectEvent()##{nameof(Conf.FilterOnActionEffectEvent)}", ref Conf.FilterOnActionEffectEvent);
        ImGui.Checkbox($"OnActionEffectApplied()##{nameof(Conf.FilterOnActionEffectApplied)}", ref Conf.FilterOnActionEffectApplied);
        ImGui.Indent();
        ImGui.Checkbox($"Filter Players##{nameof(Conf.FilterOnActionEffectAppliedSubFilterPlayers)}", ref Conf.FilterOnActionEffectAppliedSubFilterPlayers);
        ImGui.Checkbox($"Filter Enemies##{nameof(Conf.FilterOnActionEffectAppliedSubFilterEnemies)}", ref Conf.FilterOnActionEffectAppliedSubFilterEnemies);
        ImGui.Checkbox($"Filter To Source##{nameof(Conf.FilterOnActionEffectAppliedSubFilterSource)}", ref Conf.FilterOnActionEffectAppliedSubFilterSource);
        ImGui.Checkbox($"Filter Without ActionEffect##{nameof(Conf.FilterOnActionEffectAppliedSubFilterNoActionEffect)}", ref Conf.FilterOnActionEffectAppliedSubFilterNoActionEffect);
        ImGui.Unindent();
        ImGui.Checkbox($"OnGainBuffEffect()##{nameof(Conf.FilterOnGainBuffEffect)}", ref Conf.FilterOnGainBuffEffect);
        ImGui.Indent();
        ImGui.Checkbox($"Filter Players##{nameof(Conf.FilterOnGainBuffEffectSubFilterPlayers)}", ref Conf.FilterOnGainBuffEffectSubFilterPlayers);
        ImGui.Checkbox($"Filter Enemies##{nameof(Conf.FilterOnGainBuffEffectSubFilterEnemies)}", ref Conf.FilterOnGainBuffEffectSubFilterEnemies);
        ImGui.Unindent();
        ImGui.Checkbox($"OnRemoveBuffEffect()##{nameof(Conf.FilterOnRemoveBuffEffect)}", ref Conf.FilterOnRemoveBuffEffect);
        ImGui.Indent();
        ImGui.Checkbox($"Filter Players##{nameof(Conf.FilterOnRemoveBuffEffectSubFilterPlayers)}", ref Conf.FilterOnRemoveBuffEffectSubFilterPlayers);
        ImGui.Checkbox($"Filter Enemies##{nameof(Conf.FilterOnRemoveBuffEffectSubFilterEnemies)}", ref Conf.FilterOnRemoveBuffEffectSubFilterEnemies);
        ImGui.Unindent();
        ImGui.Checkbox($"OnUpdateBuffEffect()##{nameof(Conf.FilterOnUpdateBuffEffect)}", ref Conf.FilterOnUpdateBuffEffect);
        ImGui.Indent();
        ImGui.Checkbox($"Filter Players##{nameof(Conf.FilterOnUpdateBuffEffectSubFilterPlayers)}", ref Conf.FilterOnUpdateBuffEffectSubFilterPlayers);
        ImGui.Checkbox($"Filter Enemies##{nameof(Conf.FilterOnUpdateBuffEffectSubFilterEnemies)}", ref Conf.FilterOnUpdateBuffEffectSubFilterEnemies);
        ImGui.Unindent();
        ImGui.Checkbox($"OnReset()##{nameof(Conf.FilterOnReset)}", ref Conf.FilterOnReset);
    }


    public class Config
    {
        public bool WriteToFile = true;   // 専用ファイル (pluginConfigs/Splatoon/ScriptEventLogger.log) に書く
        public bool AiLog = false;        // AI 用ログ (ScriptEventLogger.ai.log、人が読む用ではない。フィルタに関係なく全部)
        public bool FilterOnSetup = false;
        public bool FilterOnEnable = false;
        public bool FilterOnDisable = false;
        public bool FilterOnCombatStart = false;
        public bool FilterOnCombatEnd = false;
        public bool FilterOnPhaseChange = false;
        public bool FilterOnMapEffect = true;
        public bool FilterOnObjectEffect = false;
        public bool FilterOnTetherCreate = true;
        public bool FilterOnTetherRemoval = true;
        public bool FilterOnVFXSpawn = true;
        public bool FilterOnVFXSpawnSubFilterPlayers = false;
        public bool FilterOnVFXSpawnSubFilterEnemies = false;
        public bool FilterOnStartingCast = true;
        public bool FilterOnMessage = false;
        public bool FilterOnDirectorUpdate = false;
        public bool FilterOnObjectCreation = false;
        public bool FilterOnActorControl = false;
        public bool FilterOnActionEffectEvent = true;
        public bool FilterOnActionEffectApplied = false;
        public bool FilterOnActionEffectAppliedSubFilterPlayers = false;
        public bool FilterOnActionEffectAppliedSubFilterEnemies = false;
        public bool FilterOnActionEffectAppliedSubFilterSource = false;
        public bool FilterOnActionEffectAppliedSubFilterNoActionEffect = true;
        public bool FilterOnGainBuffEffect = false;
        public bool FilterOnGainBuffEffectSubFilterPlayers = false;
        public bool FilterOnGainBuffEffectSubFilterEnemies = false;
        public bool FilterOnRemoveBuffEffect = false;
        public bool FilterOnRemoveBuffEffectSubFilterPlayers = false;
        public bool FilterOnRemoveBuffEffectSubFilterEnemies = false;
        public bool FilterOnUpdateBuffEffect = false;
        public bool FilterOnUpdateBuffEffectSubFilterPlayers = false;
        public bool FilterOnUpdateBuffEffectSubFilterEnemies = false;
        public bool FilterOnReset = false;

        public void Reset()
        {
            FilterOnSetup = false;
            FilterOnEnable = false;
            FilterOnDisable = false;
            FilterOnCombatStart = false;
            FilterOnCombatEnd = false;
            FilterOnPhaseChange = false;
            FilterOnMapEffect = true;
            FilterOnObjectEffect = false;
            FilterOnTetherCreate = true;
            FilterOnTetherRemoval = true;
            FilterOnVFXSpawn = true;
            FilterOnVFXSpawnSubFilterPlayers = false;
            FilterOnVFXSpawnSubFilterEnemies = false;
            FilterOnStartingCast = true;
            FilterOnMessage = false;
            FilterOnDirectorUpdate = false;
            FilterOnObjectCreation = false;
            FilterOnActorControl = false;
            FilterOnActionEffectEvent = true;
            FilterOnActionEffectApplied = false;
            FilterOnActionEffectAppliedSubFilterPlayers = false;
            FilterOnActionEffectAppliedSubFilterEnemies = false;
            FilterOnActionEffectAppliedSubFilterSource = false;
            FilterOnActionEffectAppliedSubFilterNoActionEffect = true;
            FilterOnGainBuffEffect = false;
            FilterOnGainBuffEffectSubFilterPlayers = false;
            FilterOnGainBuffEffectSubFilterEnemies = false;
            FilterOnRemoveBuffEffect = false;
            FilterOnRemoveBuffEffectSubFilterPlayers = false;
            FilterOnRemoveBuffEffectSubFilterEnemies = false;
            FilterOnUpdateBuffEffect = false;
            FilterOnUpdateBuffEffectSubFilterPlayers = false;
            FilterOnUpdateBuffEffectSubFilterEnemies = false;
            FilterOnReset = false;
        }
    }
}
