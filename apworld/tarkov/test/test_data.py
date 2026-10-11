import unittest

from ..data import load_data
from ..items import item_table
from .. import location_table, TarkovWorld


class TestData(unittest.TestCase):
    def test_item_ids_unique(self) -> None:
        ids = [info.code for info in item_table.values()]
        self.assertEqual(len(ids), len(set(ids)))

    def test_location_ids_unique(self) -> None:
        ids = list(location_table.values())
        self.assertEqual(len(ids), len(set(ids)))

    def test_location_names_unique(self) -> None:
        data = load_data()
        names = [q["location_name"] for q in data["quests"] if q["check"]]
        names += [h["name"] for h in data["hideout"]] + [e["name"] for e in data["extracts"]]
        self.assertEqual(len(names), len(set(names)))

    def test_items_and_locations_do_not_overlap(self) -> None:
        self.assertFalse(set(TarkovWorld.item_name_to_id.values()) & set(TarkovWorld.location_name_to_id.values()))

    def test_every_check_quest_is_possible_and_gated(self) -> None:
        for q in load_data()["quests"]:
            if q["check"]:
                self.assertTrue(q["possible"], q["name"])
                self.assertIsNotNone(q["trader"], q["name"])

    def test_prerequisites_exist(self) -> None:
        quests = {q["id"] for q in load_data()["quests"]}
        for q in load_data()["quests"]:
            for p in q["prereqs"]:
                if q["possible"]:
                    self.assertIn(p["id"], quests, f"{q['name']} requires unknown quest {p['id']}")

    def test_hideout_prerequisites_resolve(self) -> None:
        data = load_data()
        present = {(h["area"], h["level"]) for h in data["hideout"]}
        for h in data["hideout"]:
            for area, level in h["prereq_areas"]:
                # either another check, or a level a Standard profile already has (stash 1)
                self.assertTrue((area, level) in present or (area, level) == (3, 1), (h["name"], area, level))
