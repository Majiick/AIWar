using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Loaders;
using MoonSharp.Interpreter.Serialization.Json;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace AI_War
{
	public enum API_RETURNS
	{
		OK = 0,
		ERROR = -1,
		NOT_FOUND = -2,
		NO_PERMISSION = -3,
		INVALID_ARGUMENTS = -4,
	}

	public abstract class IGameObject {
		public int id { get; set; }
		public string owner { get; set; }
		public abstract string type { get; }
		public int x { get; set; }
		public int y { get; set; }

		virtual public Table Table(Script owner) {
			var t = new Table(owner);
			t.Set("id", DynValue.NewNumber(id));
			t.Set("owner", DynValue.NewString(this.owner));
			t.Set("type", DynValue.NewString(type));
			t.Set("x", DynValue.NewNumber(x + 1));
			t.Set("y", DynValue.NewNumber(y + 1));

			return t;
		}
	}

	public class Ship : IGameObject {
		public override string type => "ship";
		public int bombsAvailable = 3;

		public override Table Table(Script owner) {
			var t = base.Table(owner);
			t.Set("bombs_available", DynValue.NewNumber(bombsAvailable));
			return t;
		}
	}

	public class Bomb : IGameObject {
		public override string type => "bomb";
	}

	public class Explosion : IGameObject {
		public override string type => "explosion";
		public long tickMade { get; set; }
	}

	public class Memory {
		public string json;
	}

	public class Cell : IEnumerable {
		public List<IGameObject> contents = new List<IGameObject>();

		public IEnumerator GetEnumerator() {
			return contents.GetEnumerator();
		}
	}

	public class Map {
		public Cell this[int x, int y] {
			get => cells[x, y];
			set => cells[x, y] = value;
		}

		public const int WIDTH = 20;
		public const int HEIGHT = 20;
		public Cell[,] cells = new Cell[WIDTH, HEIGHT];
		
		public Map() {
			for (int x = 0; x < WIDTH; x++) {
				for (int y = 0; y < HEIGHT; y++) {
					cells[x, y] = new Cell();
				}
			}
		}

		public DynValue LuaMap(Script s) {
			DynValue.NewTable(s, new DynValue { });
			var map = new Table(s);
		    for (int x = 0; x < WIDTH; x++) {
				var xTable = new Table(s);
				map.Set(x + 1, DynValue.NewTable(xTable));
				for (int y = 0; y < HEIGHT; y++) {
					var contents = new Table(s);
					int contentCount = 1;
					foreach (IGameObject g in cells[x, y]) {
						contents.Set(contentCount, DynValue.NewTable(g.Table(s)));
						contentCount++;
					}
					xTable.Set(y + 1, DynValue.NewTable(contents));
				}
			}

			var m = DynValue.NewTable(map);
			return m;
		}

		public string JsonMap() {
			return JsonTableConverter.ObjectToJson(LuaMap(new Script()).Table);
		}
	}

	public class GameApi {
		Map map;
		public GameApi(Map map) {
			this.map = map;
		}

		public class Event {}
		public ConcurrentBag<Event> moveAndPlaceBombEvents = new ConcurrentBag<Event>();

		public class PlaceBombEvent : Event {
			public Bomb bomb;
		}
		private DynValue PlaceBomb(string playerName, Script ownerScript) {
			Table ret = new Table(ownerScript);
			ret.Set(1, DynValue.Nil);
			ret.Set(2, DynValue.NewNumber((int)API_RETURNS.ERROR));

			Ship playerShip = null;
			foreach (Cell cell in map.cells.Cast<Cell>()) {
				foreach (IGameObject go in cell) {
					if (go is Ship && ((Ship)go).owner == playerName) {
						playerShip = go as Ship;
						break;
					}
				}
			}

			if (playerShip == null) {
				return DynValue.NewTable(ret);
			}
			if (playerShip.bombsAvailable <= 0) {
				return DynValue.NewTable(ret);
			}

			playerShip.bombsAvailable--;
			int id = IDCounter.NewID();
			var bomb = new Bomb();
			bomb.id = id;
			bomb.owner = playerName;
			bomb.x = playerShip.x;
			bomb.y = playerShip.y;
			moveAndPlaceBombEvents.Add(new PlaceBombEvent { bomb = bomb });

			ret.Set(1, DynValue.NewTable(bomb.Table(ownerScript)).CloneAsWritable());
			ret.Set(2, DynValue.NewNumber((int)API_RETURNS.OK));
			return DynValue.NewTable(ret);
		}
		public Func<DynValue> PlaceBombBind(string playerName, Script ownerScript) {
			return () => PlaceBomb(playerName, ownerScript);
		}

		public class BombExplodeEvent {
			public Bomb bomb;
		}
		public ConcurrentBag<BombExplodeEvent> bombExplodeEvents = new ConcurrentBag<BombExplodeEvent>();
		private int ExplodeBomb(string playerName, int bombID) {
			Ship playerShip = null;
			foreach (Cell cell in map.cells.Cast<Cell>()) {
				foreach (IGameObject go in cell) {
					if (go is Ship && ((Ship)go).owner == playerName) {
						playerShip = go as Ship;
						break;
					}
				}
			}
			if (playerShip != null) {
				playerShip.bombsAvailable++;
			}
			Bomb bombToExplode = null;
			foreach (Cell cell in map.cells.Cast<Cell>()) {
				foreach (IGameObject go in cell) {
					if (go.id == bombID && go is Bomb) {
						bombToExplode = go as Bomb;
						break;
					}
				}
			}
			if (bombToExplode == null) {
				return (int)API_RETURNS.NOT_FOUND;
			}
			if (bombToExplode.owner != playerName) {
				return (int)API_RETURNS.NO_PERMISSION;
			}
			bombExplodeEvents.Add(new BombExplodeEvent { bomb = bombToExplode });
			return (int)API_RETURNS.OK;
		}

		public Func<int, int> ExplodeBomb(string playerName) {
			return (t2) => ExplodeBomb(playerName, t2);
		}

		private DynValue MyShip(string playerName, Script ownerScript) {
			Table ret = new Table(ownerScript);
			ret.Set(1, DynValue.Nil);
			ret.Set(2, DynValue.NewNumber((int)API_RETURNS.NOT_FOUND));
			foreach (Cell cell in map.cells.Cast<Cell>()) {
				foreach (IGameObject go in cell) {
					if (go is Ship && ((Ship)go).owner == playerName) {
						ret.Set(1, DynValue.NewTable(go.Table(ownerScript)));
						ret.Set(2, DynValue.NewNumber((int)API_RETURNS.OK));
						break;
					}
				}
			}
			
			return DynValue.NewTable(ret);
		}

		public Func<DynValue> MyShipBind(string playerName, Script ownerScript) {
			return () => MyShip(playerName, ownerScript);
		}

		public class MoveEvent : Event {
			public IGameObject go;
			public int oldX;
			public int oldY;
		}
		private int Move(string player, int objID, int x, int y) {
			IGameObject objToMove = null;
			foreach (Cell cell in map.cells.Cast<Cell>()) {
				foreach (IGameObject go in cell) {
					if (go.id == objID) {
						objToMove = go;
						break;
					}
				}
			}
			if (objToMove == null) {
				return (int)API_RETURNS.NOT_FOUND;
			}
			if (objToMove.owner != player) {
				return (int)API_RETURNS.NO_PERMISSION;
			}
			if (!(objToMove is Ship)) {
				return (int)API_RETURNS.INVALID_ARGUMENTS;
			}

			int newX = objToMove.x + x;
			int newY = objToMove.y + y;
			if (newX >= Map.WIDTH || newY >= Map.HEIGHT || newX < 0 || newY < 0) {
				return (int)API_RETURNS.INVALID_ARGUMENTS;
			}
			moveAndPlaceBombEvents.Add(new MoveEvent { go = objToMove, oldX = objToMove.x, oldY = objToMove.y });
			objToMove.x = newX;
			objToMove.y = newY;
			return (int)API_RETURNS.OK;
		}

		public Func<int, int, int, int> Move(string playerName) {
			return (t2, t3, t4) => Move(playerName, t2, t3, t4);
		}

		public ConcurrentBag<AddShipEvent> addShipBag = new ConcurrentBag<AddShipEvent>();
		public class AddShipEvent {
			public int id;
			public string owner;
			public int x;
			public int y;
		}
		private DynValue CreateShip(string playerName, Script ownerScript, int x, int y) {
			Table ret = new Table(ownerScript);
			ret.Set(1, DynValue.Nil);
			ret.Set(2, DynValue.NewNumber((int)API_RETURNS.NOT_FOUND));
			if (x > Map.WIDTH || y > Map.HEIGHT || x <= 0 || y <= 0) {
				ret.Set(2, DynValue.NewNumber((int)API_RETURNS.INVALID_ARGUMENTS));
				return DynValue.NewTable(ret);
			}
			foreach(Cell cell in map.cells.Cast<Cell>()) {  // Check if ship already exists on map.
				foreach(IGameObject go in cell) {
					if (go is Ship && ((Ship)go).owner == playerName) {
						ret.Set(2, DynValue.NewNumber((int)API_RETURNS.ERROR));
						return DynValue.NewTable(ret);
					}
				}
			}

			if (addShipBag.Any(x => x.owner == playerName)) {  // Check if already tried to create ship this tick.
				ret.Set(2, DynValue.NewNumber((int)API_RETURNS.ERROR));
				return DynValue.NewTable(ret);
			}

			int id = IDCounter.NewID();
			addShipBag.Add(new AddShipEvent { id = id, owner = playerName, x = x, y = y });
			ret.Set(1, DynValue.NewNumber(id));
			ret.Set(2, DynValue.NewNumber((int)API_RETURNS.OK));
			return DynValue.NewTable(ret);
		}
		public Func<int, int, DynValue> CreateShip(string playerName, Script owner) {
			return (t2, t3) => CreateShip(playerName, owner, t2, t3);
		}
	}

	public class Player {
		public string name;
		public string script;
		public Memory memory;
	}

	static class IDCounter {
		private static int idCounter = 1000;  // Set to 1000 so it doesn't clash with returning error codes.
		public static int NewID() {
			return Interlocked.Increment(ref idCounter);
		}
	}

	static class RandomSeed
	{
		private static int num = 0;
		public static int NewNumber() {
			return Interlocked.Increment(ref num);
		}
	}

    class AIWar {
		public static Map map;
		public static GameApi api;
		public static long tick;
		public static ConnectionMultiplexer redis;
		
		static void PrewarmMoonsharp() {
			string script = @"    
		-- defines a factorial function
		function fact (n)
			if (n == 0) then
				return 1
			else
				return n*fact(n - 1)
			end
		end

		return fact(5)";
			
			Script.RunString(script);
		}

		public static Script InitializeScript(Player p) {
			Script script = new Script(CoreModules.Preset_SoftSandbox);

			((ScriptLoaderBase)script.Options.ScriptLoader).ModulePaths = ScriptLoaderBase.UnpackStringPaths("C:\\Users\\Ecoste\\Desktop\\AIWar\\AIWar\\?.lua");
			script.Globals["move"] = api.Move(p.name);
			script.Globals["create_ship"] = api.CreateShip(p.name, script);
			script.Globals["map"] = map.LuaMap(script);
			script.Globals["my_ship"] = api.MyShipBind(p.name, script);
			script.Globals["place_bomb"] = api.PlaceBombBind(p.name, script);
			script.Globals["incrementing_number"] = (Func<int>)(() => { return RandomSeed.NewNumber(); });
			script.Globals["explode"] = api.ExplodeBomb(p.name);
			if (!String.IsNullOrEmpty(p.memory.json)) {
				var t = JsonTableConverter.JsonToTable(p.memory.json);
				script.Globals["memory"] = JsonTableConverter.JsonToTable(p.memory.json, script);
			}
			script.DoFile("C:\\Users\\Ecoste\\Desktop\\AIWar\\AIWar\\player_base.lua");
			return script;
		}

		struct ScriptRunResult {
			public DynValue result;
			public long executionTime;  // In milliseconds
			public Exception error;
		}

		// Returns the DynValue return and the amount of milliseconds it took to run.
		static Task<ScriptRunResult> RunScriptAsync(Player player, oneOffScript oneOffOverride = null) {
			return Task.Run(() => {
				var watch = new Stopwatch();
				watch.Start();
				var s = InitializeScript(player);
				DynValue r = null;
				Exception error = null;
				try {
					if (oneOffOverride != null && !String.IsNullOrEmpty(oneOffOverride.script)) {
						r = s.DoString(oneOffOverride.script);
					} else {
						r = s.DoString(player.script);
					}
				} catch (Exception e) {
					Console.WriteLine("Error: " + e.Message);
					error = e;
				}
				if (s.Globals["memory"] != null) {
					player.memory.json = JsonTableConverter.TableToJson(s.Globals["memory"] as Table);
					Console.WriteLine("memory: " + player.memory.json);
				}
				if (oneOffOverride != null) {
					oneOffOverride.executed = true;
				}
				return new ScriptRunResult { result=r, executionTime=watch.ElapsedMilliseconds, error=error };
			});
		}

		static void WritePlayerError(string playerName, string error) {
			redis.GetDatabase().StringSet(playerName + "_error", error);
		}

		static void RunAllScripts(List<Player> players, ConcurrentDictionary<Player, oneOffScript> oneOffs) {
			// Each script will do a bunch of events such as move and attack.
			// These events need to all be connected and at the end all resolved.
			List<(Player, Task<ScriptRunResult>)> tasks = new List<(Player, Task<ScriptRunResult>)>();
			players.ForEach(p => tasks.Add((p, RunScriptAsync(p))));
			foreach (var kv in oneOffs) {
				if (!kv.Value.executed) {
					tasks.Add((kv.Key, RunScriptAsync(kv.Key, oneOffOverride: kv.Value)));
				}
			}
			Thread.Sleep(1000);
			

			foreach (var t in tasks) {
				if (t.Item2.Result.error != null) {
					WritePlayerError(t.Item1.name, t.Item2.Result.error.Message);
				} else if (t.Item2.IsCompleted) {
					Console.WriteLine("Script completed, took " + t.Item2.Result.executionTime + " ms.");
				} else {
					WritePlayerError(t.Item1.name, "Exceeded 1 second limit.");
				}
			}
		}

		static void ExplodeBomb(Bomb b) {
			map[b.x, b.y].contents.Remove(b);
			map[b.x, b.y].contents.Add(new Explosion { tickMade = tick, x = b.x, y = b.y });
			for (int x = -2; x < 3; x++) {
				for (int y = -2; y < 3; y++) {
					int targetX = b.x + x;
					int targetY = b.y + y;
					if (targetX >= Map.WIDTH || targetY >= Map.HEIGHT || targetX < 0 || targetY < 0) {
						continue;
					}
					List<Ship> toKill = new List<Ship>();
					List<Bomb> toExplodeOtherBombs = new List<Bomb>();
					foreach (IGameObject go in map[targetX, targetY].contents) {
						if (go is Ship) {
							toKill.Add(go as Ship);
						}
						if (go is Bomb) {
							toExplodeOtherBombs.Add(go as Bomb);
						}
					}
					foreach (Bomb bte in toExplodeOtherBombs) {
						ExplodeBomb(bte);
					}
					foreach (Ship ship in toKill) {
						map[ship.x, ship.y].contents.Remove(ship);
						List<Bomb> toExplodeKilledPlayer = new List<Bomb>();
						foreach (Cell cell in map.cells.Cast<Cell>()) {  // Explode all bombs of the killed player.
							foreach (IGameObject go in cell) { 
								if (go is Bomb && go.owner == ship.owner) {
									toExplodeKilledPlayer.Add((Bomb)go);
								}
							}
						}
						foreach (Bomb bte in toExplodeKilledPlayer) {
							ExplodeBomb(bte);
						}
					}
				}
			}
		}

		static void ResolveEvents() {
			foreach (var e in api.moveAndPlaceBombEvents) {
				if (e is GameApi.MoveEvent) {
					var me = e as GameApi.MoveEvent;
					map.cells[me.oldX, me.oldY].contents.Remove(me.go);
					map.cells[me.go.x, me.go.y].contents.Add(me.go);
				}
				if (e is GameApi.PlaceBombEvent) {
					GameApi.PlaceBombEvent pbe = e as GameApi.PlaceBombEvent;
					map.cells[pbe.bomb.x, pbe.bomb.y].contents.Add(pbe.bomb);
				}
			}
			api.moveAndPlaceBombEvents.Clear();

			foreach (var e in api.bombExplodeEvents) {
				ExplodeBomb(e.bomb);
			}
			api.bombExplodeEvents.Clear();

			foreach (var se in api.addShipBag) {
				var ship = new Ship();
				ship.id = se.id;
				ship.owner = se.owner;
				ship.x = se.x - 1;
				ship.y = se.y - 1;
				map[ship.x, ship.y].contents.Add(ship);
			}
			api.addShipBag.Clear();
		}

		static void AddPlayers(ref List<Player> players) {
			foreach (string key in redis.GetServer(redis.GetEndPoints()[0]).Keys(pattern: "*_script")) {
				string user = key.Replace("_script", "");
				string script = redis.GetDatabase().StringGet(key);
				var player = players.FirstOrDefault(x => x.name == user);
				if (player == null) {
					players.Add(new Player { script = script, name = user, memory = new Memory() });
				} else {
					player.script = script;  // Update script
				}
			}
		}

		static void CleanUpExplosions() {
			// This should be in the gameObject.Update or something.
			List<Explosion> toRemove = new List<Explosion>();
			foreach (Cell cell in map.cells.Cast<Cell>()) {  // Check if ship already exists on map.
				foreach (IGameObject go in cell) {
					if (go is Explosion) {
						Explosion e = go as Explosion;
						if (tick > e.tickMade) {
							toRemove.Add(e);
						}
					}
				}
			}

			foreach (Explosion e in toRemove) {
				map[e.x, e.y].contents.Remove(e);
			}
		}

		static void StartRedis() {
			// Data:
			// Keys 
			//   latest_map = latest_map_json_string
			//   <player_name>_script = player_lua_script_string
			//   <player_name>_error = latest_player_error_string

			Process.Start(@"C:\Users\Ecoste\Desktop\AIWar\AIWar\redis\redis-server.exe");
			redis = ConnectionMultiplexer.Connect("localhost:6379");
			Debug.Assert(redis.IsConnected);
			Console.WriteLine("Redis started and connected.\n\n");
		}

		static void DebugAddScript(ref List<Player> players, string user, string script) {
			players.Add(new Player { script = script, name = user, memory = new Memory() });
		}
		public class oneOffScript {
			public string script;
			public bool executed;
		}
		static ConcurrentDictionary<Player, oneOffScript> oneOffs = new ConcurrentDictionary<Player, oneOffScript>();
		static void FilterExecutedOneOffs() {
			List<Player> toRemove = new List<Player>();
			foreach(var kv in oneOffs) {
				if (kv.Value.executed) {
					toRemove.Add(kv.Key);
				}
			}

			foreach (var k in toRemove) {
				oneOffScript of;
				oneOffs.TryRemove(k, out of);
			}
		}
		static void Main(string[] args) {
			StartRedis();
			List<Player> players = new List<Player>();
			redis.GetSubscriber().Subscribe("oneoff", (channel, message) => {
				dynamic obj = JsonConvert.DeserializeObject((string)message);
				var player = players.FirstOrDefault(x => x.name == (string)obj.user);
				if (player != null) {
					oneOffs.TryAdd(player, new oneOffScript { script = (string)obj.code });
				}
			});

			PrewarmMoonsharp();
			map = new Map();
			api = new GameApi(map);
			while (true) {
				AddPlayers(ref players);
				RunAllScripts(players, oneOffs);
				ResolveEvents();
				CleanUpExplosions();
				redis.GetDatabase().StringSet("latest_map", map.JsonMap());
				tick++;
				FilterExecutedOneOffs();
				GC.Collect();
				GC.WaitForPendingFinalizers();
			}
		}
    }
}
