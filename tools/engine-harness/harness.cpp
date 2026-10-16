// YGO Card Crafter engine harness.
//
// Runs generated card scripts inside the real open-source ygopro engine (ocgcore) and plays a short,
// scripted duel with an automatic player. It proves that scripts load, that their activation checks
// run, and that their effects resolve and change the board as expected.
//
// Usage: harness <scenario file> <script dir> [<script dir>...]
//
// Scenario format, one command per line (# starts a comment):
//   cdb <path>                         card database to read card data from (repeatable)
//   card <code> <player> <location> [count]
//                                      put cards in place before the duel; location is
//                                      deck|hand|mzone|szone|grave|removed|extra
//   activate <code> [times]            whoever controls this card activates its effects whenever offered
//   summon <code>                      its controller Normal Summons this card when it can
//   spsummon <code>                    its controller uses this card's summon procedure when it can
//   attack <code>                      in the Battle Phase, this monster attacks once (the first target offered)
//   announce <code>                    the card name declared for "declare 1 card name" (default 1004)
//   turns <n>                          how many turns to play before checking (default 1)
//   setup <lua statement>              runs once after the cards are placed, before the duel's first step
//                                      (e.g. putting counters on a card); repeatable, run in order
//   check <lua expression>             must be true after the last turn; player 0 is "0"
//   expect_activations <n>             how many activations must have happened (default: no check)

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <map>
#include <sstream>
#include <string>
#include <vector>
#include <sqlite3.h>

#include "ocgapi.h"
#include "duel.h"
#include "field.h"
#include "card.h"
#include "effect.h"
#include "interpreter.h"

static std::vector<std::string> g_script_dirs;
static std::map<uint32_t, card_data> g_cards;
static std::vector<char> g_script_buf;
static int g_errors = 0;

static uint8_t* harness_read_script(const char* name, int* len) {
	std::string n = name;
	if (n.rfind("./script/", 0) == 0) n = n.substr(9);
	for (auto& dir : g_script_dirs) {
		std::ifstream f(dir + "/" + n, std::ios::binary);
		if (!f) continue;
		g_script_buf.assign(std::istreambuf_iterator<char>(f), std::istreambuf_iterator<char>());
		*len = (int)g_script_buf.size();
		return (uint8_t*)g_script_buf.data();
	}
	return nullptr;
}

static uint32_t harness_read_card(uint32_t code, card_data* data) {
	data->clear();
	auto it = g_cards.find(code);
	if (it == g_cards.end()) return 0;
	*data = it->second;
	return 0;
}

static uint32_t on_message(intptr_t pduel, uint32_t type) {
	char buf[256];
	get_log_message(pduel, buf);
	std::string msg = buf;
	// Cards with no script at all (filler cards) are expected to have none.
	if (msg.find("No such file") != std::string::npos || msg.find("cannot open") != std::string::npos) return 0;
	std::printf("SCRIPT ERROR: %s\n", buf);
	++g_errors;
	return 0;
}

static bool load_cdb(const std::string& path) {
	sqlite3* db = nullptr;
	if (sqlite3_open_v2(path.c_str(), &db, SQLITE_OPEN_READONLY, nullptr) != SQLITE_OK) return false;
	sqlite3_stmt* st = nullptr;
	const char* sql = "SELECT id, alias, setcode, type, atk, def, level, race, attribute FROM datas";
	if (sqlite3_prepare_v2(db, sql, -1, &st, nullptr) != SQLITE_OK) { sqlite3_close(db); return false; }
	while (sqlite3_step(st) == SQLITE_ROW) {
		card_data d{};
		d.code = (uint32_t)sqlite3_column_int64(st, 0);
		d.alias = (uint32_t)sqlite3_column_int64(st, 1);
		write_setcode(d.setcode, (uint64_t)sqlite3_column_int64(st, 2));
		d.type = (uint32_t)sqlite3_column_int64(st, 3);
		d.attack = (int32_t)sqlite3_column_int64(st, 4);
		d.defense = (int32_t)sqlite3_column_int64(st, 5);
		if (d.type & TYPE_LINK) { d.link_marker = d.defense; d.defense = 0; }
		uint32_t level = (uint32_t)sqlite3_column_int64(st, 6);
		d.level = level & 0xff;
		d.lscale = (level >> 24) & 0xff;
		d.rscale = (level >> 16) & 0xff;
		d.race = (uint32_t)sqlite3_column_int64(st, 7);
		d.attribute = (uint32_t)sqlite3_column_int64(st, 8);
		g_cards[d.code] = d;
	}
	sqlite3_finalize(st);
	sqlite3_close(db);
	return true;
}

struct Placement { uint32_t code; int player; uint8_t location; int count; };

struct Scenario {
	std::vector<Placement> cards;
	std::map<uint32_t, int> activate;
	std::vector<uint32_t> summon, spsummon, attack;
	uint32_t announce = 0;
	std::vector<std::string> checks;
	std::vector<std::string> setup;
	int turns = 1;
	int expect_activations = -1;
};

static uint8_t parse_location(const std::string& s) {
	if (s == "deck") return LOCATION_DECK;
	if (s == "hand") return LOCATION_HAND;
	if (s == "mzone") return LOCATION_MZONE;
	if (s == "szone") return LOCATION_SZONE;
	if (s == "grave") return LOCATION_GRAVE;
	if (s == "removed") return LOCATION_REMOVED;
	if (s == "extra") return LOCATION_EXTRA;
	return 0;
}

static bool parse_scenario(const char* path, Scenario& sc) {
	std::ifstream f(path);
	if (!f) return false;
	std::string line;
	while (std::getline(f, line)) {
		if (line.empty() || line[0] == '#') continue;
		std::istringstream in(line);
		std::string cmd;
		in >> cmd;
		if (cmd == "cdb") {
			std::string p; std::getline(in >> std::ws, p);
			if (!load_cdb(p)) { std::printf("cannot read cdb %s\n", p.c_str()); return false; }
		}
		else if (cmd == "card") {
			Placement p{}; std::string loc; p.count = 1;
			in >> p.code >> p.player >> loc >> p.count;
			if (p.count <= 0) p.count = 1;
			p.location = parse_location(loc);
			if (!p.location) { std::printf("bad location %s\n", loc.c_str()); return false; }
			sc.cards.push_back(p);
		}
		else if (cmd == "activate") { uint32_t c; int t = 1; in >> c >> t; if (t <= 0) t = 1; sc.activate[c] = t; }
		else if (cmd == "summon") { uint32_t c; in >> c; sc.summon.push_back(c); }
		else if (cmd == "spsummon") { uint32_t c; in >> c; sc.spsummon.push_back(c); }
		else if (cmd == "attack") { uint32_t c; in >> c; sc.attack.push_back(c); }
		else if (cmd == "announce") { in >> sc.announce; }
		else if (cmd == "turns") in >> sc.turns;
		else if (cmd == "expect_activations") in >> sc.expect_activations;
		else if (cmd == "check") { std::string rest; std::getline(in, rest); sc.checks.push_back(rest); }
		else if (cmd == "setup") { std::string rest; std::getline(in, rest); sc.setup.push_back(rest); }
		else { std::printf("unknown command %s\n", cmd.c_str()); return false; }
	}
	return true;
}

static int g_activations = 0;

static bool wants(Scenario& sc, card* c) {
	auto it = sc.activate.find(c->data.code);
	return it != sc.activate.end() && it->second > 0;
}

static void used(Scenario& sc, card* c) {
	--sc.activate[c->data.code];
	++g_activations;
	std::printf("ACTIVATE: %u\n", c->data.code);
}

static bool contains(const std::vector<uint32_t>& v, uint32_t code) {
	for (auto x : v) if (x == code) return true;
	return false;
}

// Answers whatever the engine is waiting for, using its internal state.
static void respond(intptr_t pd, Scenario& sc) {
	duel* d = (duel*)pd;
	field* f = d->game_field;
	auto& unit = f->core.units.front();
	uint8_t player = unit.arg1 & 0xff;
	uint8_t b[64]{};
	switch (unit.type) {
	case PROCESSOR_SELECT_IDLECMD: {
			{
			for (size_t i = 0; i < f->core.select_chains.size(); ++i) {
				card* c = f->core.select_chains[i].triggering_effect->get_handler();
				if (wants(sc, c)) { used(sc, c); set_responsei(pd, 5 | (int32_t)(i << 16)); return; }
			}
			for (size_t i = 0; i < f->core.spsummonable_cards.size(); ++i) {
				card* c = f->core.spsummonable_cards[i];
				if (contains(sc.spsummon, c->data.code)) { std::printf("SPSUMMON: %u\n", c->data.code); set_responsei(pd, 1 | (int32_t)(i << 16)); return; }
			}
			for (size_t i = 0; i < f->core.summonable_cards.size(); ++i) {
				card* c = f->core.summonable_cards[i];
				if (contains(sc.summon, c->data.code)) {
					std::printf("SUMMON: %u\n", c->data.code);
					sc.summon.erase(std::find(sc.summon.begin(), sc.summon.end(), c->data.code));
					set_responsei(pd, 0 | (int32_t)(i << 16));
					return;
				}
			}
		}
		// To the Battle Phase only when a monster is to attack; otherwise straight to the End Phase.
		if (f->core.to_bp && !sc.attack.empty()) { set_responsei(pd, 6); return; }
		set_responsei(pd, f->core.to_ep ? 7 : 6);
		return;
	}
	case PROCESSOR_SELECT_BATTLECMD:
			for (size_t i = 0; i < f->core.attackable_cards.size(); ++i) {
			card* c = f->core.attackable_cards[i];
			auto it = std::find(sc.attack.begin(), sc.attack.end(), c->data.code);
			if (it != sc.attack.end()) {
				std::printf("ATTACK: %u\n", c->data.code);
				sc.attack.erase(it);
				set_responsei(pd, 1 | (int32_t)(i << 16));
				return;
			}
		}
		// Main Phase 2 when a monster is still to be Summoned with its procedure.
		if (f->core.to_m2 && !sc.spsummon.empty()) { set_responsei(pd, 2); return; }
		set_responsei(pd, f->core.to_ep ? 3 : 2);
		return;
	case PROCESSOR_SELECT_CHAIN: {
		for (size_t i = 0; i < f->core.select_chains.size(); ++i) {
			if (f->core.select_chains[i].flag & CHAIN_FORCED) {
				card* c = f->core.select_chains[i].triggering_effect->get_handler();
				if (wants(sc, c)) used(sc, c);
				set_responsei(pd, (int32_t)i);
				return;
			}
		}
		for (size_t i = 0; i < f->core.select_chains.size(); ++i) {
			card* c = f->core.select_chains[i].triggering_effect->get_handler();
			if (wants(sc, c)) { used(sc, c); set_responsei(pd, (int32_t)i); return; }
		}
		set_responsei(pd, -1);
		return;
	}
	case PROCESSOR_SELECT_EFFECTYN: {
		// "Activate this optional effect?" for a single trigger: yes only for cards the scenario wants.
		card* c = (card*)unit.ptarget;
		if (c && wants(sc, c)) { used(sc, c); set_responsei(pd, 1); }
		else set_responsei(pd, 0);
		return;
	}
	case PROCESSOR_SELECT_YESNO:
		set_responsei(pd, 1);
		return;
	case PROCESSOR_SELECT_OPTION:
		set_responsei(pd, 0);
		return;
	case PROCESSOR_SELECT_CARD:
	case PROCESSOR_SELECT_TRIBUTE: {
		int min = (unit.arg2) & 0xff;
		int n = std::max(1, min);
		n = std::min<int>(n, (int)f->core.select_cards.size());
		b[0] = (uint8_t)n;
		for (int i = 0; i < n; ++i) b[i + 1] = (uint8_t)i;
		set_responseb(pd, b);
		return;
	}
	case PROCESSOR_SELECT_SUM: {
		// Materials whose Levels add up to the total (Synchro, Ritual): the first set of cards that fits.
		int32_t acc = (int32_t)unit.arg1, min = (int32_t)unit.arg3, max = (int32_t)unit.arg4;
		auto& cards = f->core.select_cards;
		int must = (int)std::min<size_t>(f->core.must_select_cards.size(), 32);
		int must_sum = 0;
		for (int i = 0; i < must; ++i) {
			int32_t o1, o2;
			field::get_sum_params(f->core.must_select_cards[i]->sum_param, o1, o2);
			must_sum += o1;
		}
		int n = (int)std::min<size_t>(cards.size(), 16);
		for (uint32_t mask = 1; mask < (1u << n); ++mask) {
			int k = __builtin_popcount(mask);
			if (max && (k < min || k > max)) continue;
			std::vector<int> sums{must_sum};
			for (int i = 0; i < n; ++i) {
				if (!(mask & (1u << i))) continue;
				int32_t o1, o2;
				field::get_sum_params(cards[i]->sum_param, o1, o2);
				std::vector<int> next;
				for (int s : sums) { next.push_back(s + o1); if (o2) next.push_back(s + o2); }
				sums.swap(next);
			}
			bool ok = max ? std::find(sums.begin(), sums.end(), acc) != sums.end()
			              : std::any_of(sums.begin(), sums.end(), [acc](int s) { return s >= acc; });
			if (!ok) continue;
			int idx = 0;
			b[idx++] = (uint8_t)(must + k);
			for (int i = 0; i < must; ++i) b[idx++] = 0;
			for (int i = 0; i < n; ++i) if (mask & (1u << i)) b[idx++] = (uint8_t)i;
			set_responseb(pd, b);
			return;
		}
		set_responseb(pd, b);
		return;
	}
	case PROCESSOR_SELECT_UNSELECT_CARD: {
		bool finishable = unit.arg3 & 0xff;
		if (finishable || f->core.select_cards.empty()) { set_responsei(pd, -1); return; }
		b[0] = 1; b[1] = 0;
		set_responseb(pd, b);
		return;
	}
	case PROCESSOR_SELECT_PLACE:
	case PROCESSOR_SELECT_DISFIELD: {
		uint32_t flag = unit.arg2;
		// A set bit means "not available". Prefer the waiting player's own zones.
		for (int side = 0; side < 2; ++side) {
			for (int zone = 0; zone < 2; ++zone) {
				for (int seq = 0; seq < 8; ++seq) {
					uint32_t bit = 1u << (seq + (zone ? 8 : 0) + (side ? 16 : 0));
					if (flag & bit) continue;
					b[0] = side ? (uint8_t)(1 - player) : player;
					b[1] = zone ? LOCATION_SZONE : LOCATION_MZONE;
					b[2] = (uint8_t)seq;
					set_responseb(pd, b);
					return;
				}
			}
		}
		set_responseb(pd, b);
		return;
	}
	case PROCESSOR_SELECT_POSITION: {
		uint32_t positions = (unit.arg1 >> 16) & 0xffff;
		for (uint32_t p = 1; p <= 8; p <<= 1) if (positions & p) { set_responsei(pd, (int32_t)p); return; }
		set_responsei(pd, 1);
		return;
	}
	case PROCESSOR_ANNOUNCE_CARD:
		// "Declare 1 card name": the scenario's choice, or the opponent's Fiend.
		set_responsei(pd, (int32_t)(sc.announce ? sc.announce : 1004));
		return;
	case PROCESSOR_SORT_CARD:
		b[0] = 0xff;
		set_responseb(pd, b);
		return;
	default:
		// Pauses between steps (a prompt that was just answered, a revealed card) need no answer: answering would
		// overwrite the value the prompt returned, such as a declared card name.
		if (unit.type < PROCESSOR_ADD_CHAIN || unit.type >= 100) {
			std::printf("HARNESS: no automatic answer for prompt type %u\n", (unsigned)unit.type);
			set_responsei(pd, 0);
		}
		return;
	}
}

static void run_setup(intptr_t pd, const std::string& code) {
	lua_State* L = ((duel*)pd)->lua->lua_state;
	if (luaL_loadstring(L, code.c_str()) != LUA_OK || lua_pcall(L, 0, 0, 0) != LUA_OK) {
		std::printf("SETUP ERROR: %s -> %s\n", code.c_str(), lua_tostring(L, -1));
		lua_pop(L, 1);
		++g_errors;
		return;
	}
	std::printf("SETUP:%s\n", code.c_str());
}

static bool run_check(intptr_t pd, const std::string& expr) {
	lua_State* L = ((duel*)pd)->lua->lua_state;
	std::string code = "return (" + expr + ")";
	if (luaL_loadstring(L, code.c_str()) != LUA_OK || lua_pcall(L, 0, 1, 0) != LUA_OK) {
		std::printf("CHECK ERROR: %s -> %s\n", expr.c_str(), lua_tostring(L, -1));
		lua_pop(L, 1);
		return false;
	}
	bool ok = lua_toboolean(L, -1);
	lua_pop(L, 1);
	std::printf("CHECK %s:%s\n", ok ? "OK" : "FAIL", expr.c_str());
	return ok;
}

int main(int argc, char** argv) {
	std::setvbuf(stdout, nullptr, _IONBF, 0);
	if (argc < 3) { std::printf("usage: harness <scenario> <script dir>...\n"); return 2; }
	for (int i = 2; i < argc; ++i) g_script_dirs.push_back(argv[i]);
	Scenario sc;
	if (!parse_scenario(argv[1], sc)) return 2;

	set_script_reader(harness_read_script);
	set_card_reader(harness_read_card);
	set_message_handler(on_message);
	intptr_t pd = create_duel(12345);
	set_player_info(pd, 0, 8000, 0, 1);
	set_player_info(pd, 1, 8000, 0, 1);
	uint8_t seq[2][0x80]{};
	for (auto& p : sc.cards) {
		for (int i = 0; i < p.count; ++i) {
			uint8_t pos = POS_FACEDOWN_DEFENSE;
			if (p.location == LOCATION_MZONE || p.location == LOCATION_GRAVE || p.location == LOCATION_REMOVED) pos = POS_FACEUP_ATTACK;
			if (p.location == LOCATION_SZONE) pos = POS_FACEDOWN;
			uint8_t s = (p.location == LOCATION_MZONE || p.location == LOCATION_SZONE) ? seq[p.player][p.location]++ : 0;
			new_card(pd, p.code, (uint8_t)p.player, (uint8_t)p.player, p.location, s, pos);
		}
	}
	// Master Rule 5, deterministic shuffles.
	start_duel(pd, (5u << 16) | DUEL_PSEUDO_SHUFFLE);
	// Setup Lua runs once the cards are in place, before the first step of the duel.
	for (auto& s : sc.setup) run_setup(pd, s);

	std::vector<uint8_t> msg(0x20000);
	bool finished = false;
	for (int step = 0; step < 20000 && !finished; ++step) {
		uint32_t result = process(pd);
		get_message(pd, msg.data());
		duel* d = (duel*)pd;
			if (result & PROCESSOR_END) break;
		if (result & PROCESSOR_WAITING) {
			auto& unit = d->game_field->core.units.front();
			// Stop at the first prompt of the turn after the last one the scenario plays.
			if ((int)d->game_field->infos.turn_id > sc.turns) { finished = true; break; }
			respond(pd, sc);
		}
	}
	if (!finished) std::printf("HARNESS: duel ended or got stuck before turn %d finished\n", sc.turns);

	int failed = 0;
	for (auto& c : sc.checks) if (!run_check(pd, c)) ++failed;
	if (sc.expect_activations >= 0 && g_activations != sc.expect_activations) {
		std::printf("EXPECTED %d activations, saw %d\n", sc.expect_activations, g_activations);
		++failed;
	}
	end_duel(pd);
	std::printf("RESULT: %d script errors, %d failed checks\n", g_errors, failed);
	return (g_errors == 0 && failed == 0 && finished) ? 0 : 1;
}
