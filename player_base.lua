inspect = require("inspect")

local global_my_ship = _G.my_ship
function my_ship()  -- This is needed so we can unpack the values in Lua i.e. ship, err = my_ship()
	r = global_my_ship()
	return r[1], r[2]  -- Returns id, error.
end

local global_create_ship = _G.create_ship
function create_ship(x, y)
	r = global_create_ship(x, y)
	return r[1], r[2]  -- Returns id, error.
end