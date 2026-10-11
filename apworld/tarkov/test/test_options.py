from .bases import TarkovTestBase


class TestCollectorGoal(TarkovTestBase):
    options = {"goal": "collector", "hideout_checks": False, "extracts_per_map": 3}


class TestAllExtractsGoal(TarkovTestBase):
    options = {"goal": "all_extracts", "starting_map": "customs", "starting_traders": "random_one"}

    def test_victory_needs_every_map(self) -> None:
        self.assertFalse(self.multiworld.get_location("Escape from Tarkov", self.player).can_reach(self.multiworld.state))


class TestSmallGame(TarkovTestBase):
    options = {
        "goal_quest_count": 15,
        "max_quest_level": 15,
        "quest_traders": ["Prapor", "Therapist", "Skier"],
        "hideout_checks": False,
        "extracts_per_map": 0,
        "lock_flea_market": False,
        "trap_percentage": 50,
    }

    def test_only_selected_traders(self) -> None:
        for loc in self.multiworld.get_locations(self.player):
            if loc.address is not None:
                self.assertTrue(loc.name.split(" - ")[0] in ("Prapor", "Therapist", "Skier"), loc.name)

    def test_no_flea_item(self) -> None:
        pool = {item.name for item in self.multiworld.itempool if item.player == self.player}
        self.assertNotIn("Flea Market Access", pool)


class TestNoChecksAtStartFallsBack(TarkovTestBase):
    # Ragman-only quests with a Lighthouse start: few quests at the start; fallback must keep the seed valid
    options = {"quest_traders": ["Ragman"], "starting_map": "lighthouse", "starting_traders": "random_one",
               "hideout_checks": False, "extracts_per_map": 0, "goal_quest_count": 5}


class TestEveryStartingMap(TarkovTestBase):
    run_default_tests = False

    def test_each_start(self) -> None:
        from ..options import StartingMap
        from test.general import setup_solo_multiworld
        from .. import TarkovWorld
        for key in StartingMap.options:
            with self.subTest(start=key):
                mw = setup_solo_multiworld(TarkovWorld, ("generate_early", "create_regions", "create_items",
                                                         "set_rules", "connect_entrances", "generate_basic",
                                                         "pre_fill"))
                del mw  # construction succeeding is the test for every map


class TestStartingKit(TarkovTestBase):
    options = {"starting_kit": "replace", "starting_kit_quality": "geared"}

    def test_slot_data(self) -> None:
        data = self.world.fill_slot_data()
        self.assertEqual(data["starting_kit"], "replace")
        self.assertEqual(data["starting_kit_quality"], "geared")


class TestStartingKitDefault(TarkovTestBase):
    def test_slot_data(self) -> None:
        data = self.world.fill_slot_data()
        self.assertEqual(data["starting_kit"], "off")
        self.assertEqual(data["starting_kit_quality"], "standard")
