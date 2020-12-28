using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Loaders;
using MoonSharp.Interpreter.Serialization.Json;

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
		public abstract Table Table(Script owner);
		public int x { get; set; }
		public int y { get; set; }
	}

	public class Ship : IGameObject {
		public override string type => "ship";

		public override Table Table(Script owner) {
			var t = new Table(owner);
			t.Set("id", DynValue.NewNumber(id));
			t.Set("owner", DynValue.NewString(this.owner));
			t.Set("type", DynValue.NewString(type));
			t.Set("x", DynValue.NewNumber(x+1));
			t.Set("y", DynValue.NewNumber(y+1));
			return t;
		}
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
						contents.Set(contentCount, DynValue.NewNumber(g.id));
						contentCount++;
					}
					xTable.Set(y + 1, DynValue.NewTable(contents));
				}
			}

			var m = DynValue.NewTable(map);
			return m;
		}
	}

	public class GameApi {
		Map map;
		public GameApi(Map map) {
			this.map = map;
		}

		private DynValue MyShip(Player player) {
			Table ret = new Table(player.dynamicScript);
			ret.Set(1, DynValue.Nil);
			ret.Set(2, DynValue.NewNumber((int)API_RETURNS.NOT_FOUND));
			foreach (Cell cell in map.cells.Cast<Cell>()) {
				foreach (IGameObject go in cell) {
					if (go is Ship && ((Ship)go).owner == player.name) {
						ret.Set(1, DynValue.NewTable(go.Table(player.dynamicScript)));
						ret.Set(2, DynValue.NewNumber((int)API_RETURNS.OK));
						break;
					}
				}
			}
			
			return DynValue.NewTable(ret);
		}

		public Func<DynValue> MyShipBind(Player player) {
			return () => MyShip(player);
		}

		public class MoveEvent {
			public IGameObject go;
			public int newX;
			public int newY;
		}
		public ConcurrentBag<MoveEvent> moveBag = new ConcurrentBag<MoveEvent>();
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

			int newX = objToMove.x + x;
			int newY = objToMove.y + y;
			if (newX >= Map.WIDTH || newY >= Map.HEIGHT || newX < 0 || newY < 0) {
				return (int)API_RETURNS.INVALID_ARGUMENTS;
			}

			moveBag.Add(new MoveEvent { go = objToMove, newX = newX, newY = newY });
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
		private DynValue CreateShip(Player player, int x, int y) {
			Table ret = new Table(player.dynamicScript);
			ret.Set(1, DynValue.Nil);
			ret.Set(2, DynValue.NewNumber((int)API_RETURNS.NOT_FOUND));
			if (x > Map.WIDTH || y > Map.HEIGHT || x <= 0 || y <= 0) {
				ret.Set(2, DynValue.NewNumber((int)API_RETURNS.INVALID_ARGUMENTS));
				return DynValue.NewTable(ret);
			}
			foreach(Cell cell in map.cells.Cast<Cell>()) {  // Check if ship already exists on map.
				foreach(IGameObject go in cell) {
					if (go is Ship && ((Ship)go).owner == player.name) {
						ret.Set(2, DynValue.NewNumber((int)API_RETURNS.ERROR));
						return DynValue.NewTable(ret);
					}
				}
			}

			if (addShipBag.Any(x => x.owner == player.name)) {  // Check if already tried to create ship this tick.
				ret.Set(2, DynValue.NewNumber((int)API_RETURNS.ERROR));
				return DynValue.NewTable(ret);
			}

			int id = IDCounter.NewID();
			addShipBag.Add(new AddShipEvent { id = id, owner = player.name, x = x, y = y });
			ret.Set(1, DynValue.NewNumber(id));
			ret.Set(2, DynValue.NewNumber((int)API_RETURNS.OK));
			return DynValue.NewTable(ret);
		}
		public Func<int, int, DynValue> CreateShip(Player player) {
			return (t2, t3) => CreateShip(player, t2, t3);
		}
	}

	public struct Player {
		public string name;
		public string script;
		public Memory memory;
		public Script dynamicScript;  // Reference to the current tick's script.
	}

	static class IDCounter
	{
		public static int idCounter = 1000;  // Set to 1000 so it doesn't clash with returning error codes.
		public static int NewID() {
			return Interlocked.Increment(ref idCounter);
		}
	}

    class AIWar {
		public static Map map;
		public static GameApi api;
		
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
			p.dynamicScript = new Script();
			var s = p.dynamicScript;
			((ScriptLoaderBase)s.Options.ScriptLoader).ModulePaths = ScriptLoaderBase.UnpackStringPaths("C:\\Users\\Ecoste\\Desktop\\AIWar\\AIWar\\?.lua");
			s.Globals["move"] = api.Move(p.name);
			s.Globals["create_ship"] = api.CreateShip(p);
			s.Globals["map"] = map.LuaMap(s);
			s.Globals["my_ship"] = api.MyShipBind(p);
			s.DoFile("C:\\Users\\Ecoste\\Desktop\\AIWar\\AIWar\\player_base.lua");
			if (!String.IsNullOrEmpty(p.memory.json)) {
				Console.WriteLine("memory: " + p.memory.json);
				s.Globals["memory"] = JsonTableConverter.JsonToTable(p.memory.json);
			}
			return s;
		}

		// Returns the DynValue return and the amount of milliseconds it took to run.
		static Task<(DynValue, long)> RunScriptAsync(Player player) {
			return Task.Run(() => {
				var watch = new Stopwatch();
				watch.Start();
				var s = InitializeScript(player);
				var r = s.DoString(player.script);
				if (s.Globals["memory"] != null) {
					player.memory.json = JsonTableConverter.TableToJson(s.Globals["memory"] as Table);
					Debug.WriteLine(player.memory.json);
				}
				return (r, watch.ElapsedMilliseconds);
			});
		}

		static void RunAllScripts(List<Player> players)
		{
			// Each script will do a bunch of events such as move and attack.
			// These events need to all be connected and at the end all resolved.
			List<Task<(DynValue, long)>> tasks = new List<Task<(DynValue, long)>>();
			players.ForEach((script) => tasks.Add(RunScriptAsync(script)));
			Thread.Sleep(1000);	
			
			foreach (var t in tasks) {
				if (t.IsCompleted) {
					Console.WriteLine("Script completed, took " + t.Result.Item2 + " ms.");
				} else {
					Console.WriteLine("Script timed out");
				}
			}
		}

		static void ResolveEvents() {
			foreach (var se in api.addShipBag) {
				var ship = new Ship();
				ship.id = se.id;
				ship.owner = se.owner;
				ship.x = se.x-1;
				ship.y = se.y-1;
				map[ship.x, ship.y].contents.Add(ship);
			}
			api.addShipBag.Clear();

			foreach (var me in api.moveBag) {
				map.cells[me.go.x, me.go.y].contents.Remove(me.go);
				me.go.x = me.newX;
				me.go.y = me.newY;
				map.cells[me.go.x, me.go.y].contents.Add(me.go);
			}
			api.moveBag.Clear();
		}

		static void Main(string[] args)
        {
			PrewarmMoonsharp();
			map = new Map();
			api = new GameApi(map);
			List<Player> players = new List<Player>();
			for (int i = 0; i < 1; i++) {
				players.Add(new Player {
					name = "Player " + i.ToString(),
					memory = new Memory(),
					script= @"
ship, err = my_ship()
print(inspect(ship))
print(err)
if err != 0 then
	id, err = create_ship(5, 5)
	print(id)
    print(err)
end
print('------------')

--print(#map[5][5])"
				});
			}
			RunAllScripts(players);
			ResolveEvents();
			RunAllScripts(players);
			ResolveEvents();
			RunAllScripts(players);
			ResolveEvents();
			RunAllScripts(players);
			ResolveEvents();
		}
    }
}
