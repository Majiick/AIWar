using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Serialization.Json;
using Nancy.Json;

namespace AI_War
{
	public enum API_RETURNS
	{
		OK,
		ERROR,
	}

	public abstract class IGameObject {
		public int id { get; set; }
		public string owner { get; set; }
		public abstract string type { get; }
	}

	public class Ship : IGameObject {
		public override string type => "ship";
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

		private int MyShip(string playerName) {
			foreach (Cell cell in map.cells.Cast<Cell>()) {
				foreach (IGameObject go in cell) {
					if (go is Ship && ((Ship)go).owner == playerName) {
						return go.id;
					}
				}
			}

			return 0;
		}

		public Func<int> MyShipBind(string playerName) {
			return () => MyShip(playerName);
		}

		private int Move(string player, string objID, string dir) {
			return (int)API_RETURNS.OK;
		}

		public Func<string, string, int> Move(string playerName) {
			return (t2, t3) => Move(playerName, t2, t3);
		}

		public ConcurrentBag<AddShipEvent> addShipBag = new ConcurrentBag<AddShipEvent>();
		public class AddShipEvent {
			public int id;
			public string owner;
			public int x;
			public int y;
		}
		private int CreateShip(string playerName, int x, int y) {
			foreach(Cell cell in map.cells.Cast<Cell>()) {  // Check if ship already exists on map.
				foreach(IGameObject go in cell) {
					if (go is Ship && ((Ship)go).owner == playerName) {
						return (int)API_RETURNS.ERROR;
					}
				}
			}

			if (addShipBag.Any(x => x.owner == playerName)) {  // Check if already tried to create ship this tick.
				return (int)API_RETURNS.ERROR;
			}

			int id = IDCounter.NewID();
			addShipBag.Add(new AddShipEvent { id = id, owner = playerName, x = x, y = y });
			return id;
		}
		public Func<int, int, int> CreateShip(string playerName) {
			return (t2, t3) => CreateShip(playerName, t2, t3);
		}
	}

	public struct Player {
		public string name;
		public string script;
		public Memory memory;
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
			var s = new Script();
			s.Globals["move"] = api.Move(p.name);
			s.Globals["create_ship"] = api.CreateShip(p.name);
			s.Globals["map"] = map.LuaMap(s);
			s.Globals["my_ship"] = api.MyShipBind(p.name);
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
				map[se.x-1, se.y-1].contents.Add(ship);
			}
			api.addShipBag.Clear();
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
--if _G.memory ~= nil then
	--print(_G.memory.ship)
--end
ship = my_ship()
print('my_ship: ' .. ship)
if ship == 0 then
	ship = create_ship(5, 5)
end
print('my_ship: ' .. ship)
_G.memory = {ship=ship}
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
