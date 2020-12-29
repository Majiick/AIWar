-- inspect = require("inspect")
-- Inspect doesn't work with soft sandbox.

local global_my_ship = _G.my_ship
local global_create_ship = _G.create_ship

bombPrototype = {}
bombPrototype.explode = function(self)
	return explode(self.id)
end

shipPrototype = {}
shipPrototype.move = function(self, x, y)
	return move(self.id, x, y)
end
shipPrototype.place_bomb = function(self)
	r = place_bomb()
	if r[1] ~= nil then
		setmetatable(r[1], {__index = bombPrototype})
	end
	return r[1], r[2]  -- Returns bomb id, error.
end


function my_ship()  -- This is needed so we can unpack the values in Lua i.e. ship, err = my_ship(). Also set the metatable.
	r = global_my_ship()
	if r[1] ~= nil then
		setmetatable(r[1], {__index = shipPrototype})
	end
	return r[1], r[2]  -- Returns ship table, error.
end

function create_ship(x, y)
	r = global_create_ship(x, y)
	return r[1], r[2]  -- Returns ship id, error.
end

math.randomseed(os.time() + incrementing_number())
if _G.memory == nil then
    _G.memory = {}
end
memory = _G.memory