from typing import Dict, List, NamedTuple

from BaseClasses import Item, ItemClassification

from .data import load_data

GAME_NAME = "Escape from Tarkov"


class TarkovItem(Item):
    game = GAME_NAME


class ItemInfo(NamedTuple):
    name: str
    code: int
    kind: str
    classification: ItemClassification
    params: dict


_CLASSIFICATIONS = {
    "progression": ItemClassification.progression,
    "useful": ItemClassification.useful,
    "filler": ItemClassification.filler,
    "trap": ItemClassification.trap,
}


def _build() -> Dict[str, ItemInfo]:
    table: Dict[str, ItemInfo] = {}
    for entry in load_data()["items"]:
        table[entry["name"]] = ItemInfo(
            entry["name"], entry["id"], entry["kind"], _CLASSIFICATIONS[entry["classification"]], entry["params"]
        )
    return table


item_table: Dict[str, ItemInfo] = _build()
item_name_to_id: Dict[str, int] = {name: info.code for name, info in item_table.items()}

MAP_ITEMS: Dict[str, str] = {info.params["map"]: name for name, info in item_table.items() if info.kind == "map"}
TRADER_ITEMS: Dict[str, str] = {info.params["trader"]: name for name, info in item_table.items() if info.kind == "trader"}
FLEA_ITEM = "Flea Market Access"

# Relative weights used to fill the rest of the pool after progression items.
FILLER_WEIGHTS: Dict[str, int] = {
    "Experience Bundle": 14,
    "Large Experience Bundle": 4,
    "Prapor Reputation": 3,
    "Therapist Reputation": 3,
    "Skier Reputation": 3,
    "Peacekeeper Reputation": 3,
    "Mechanic Reputation": 3,
    "Ragman Reputation": 3,
    "Jaeger Reputation": 3,
    "Roubles (100,000)": 12,
    "Roubles (500,000)": 3,
    "Dollars (1,000)": 4,
    "Euros (1,000)": 4,
    "Weapon Crate": 8,
    "Armor Crate": 6,
    "Medical Crate": 8,
    "Ammo Crate": 8,
    "Barter Crate": 6,
    "Provisions Crate": 6,
    "Key Crate": 4,
    "Stimulant Crate": 4,
    "Roubles (25,000)": 10,
}
TRAP_ITEMS: List[str] = [name for name, info in item_table.items() if info.kind == "trap"]

item_name_groups = {
    "Maps": set(MAP_ITEMS.values()),
    "Traders": set(TRADER_ITEMS.values()),
    "Crates": {name for name, info in item_table.items() if info.kind == "loot"},
    "Money": {name for name, info in item_table.items() if info.kind == "money"},
    "Reputation": {name for name, info in item_table.items() if info.kind == "rep"},
    "Traps": set(TRAP_ITEMS),
}
