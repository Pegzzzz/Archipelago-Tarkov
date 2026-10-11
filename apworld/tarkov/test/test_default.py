from .bases import TarkovTestBase


class TestDefault(TarkovTestBase):
    def test_starting_items(self) -> None:
        names = {item.name for item in self.multiworld.precollected_items[self.player]}
        self.assertEqual(names, {"Ground Zero Access", "Prapor", "Therapist"})

    def test_progression_in_pool(self) -> None:
        pool = {item.name for item in self.multiworld.itempool if item.player == self.player}
        for name in ("Customs Access", "Woods Access", "The Lab Access", "Skier", "Jaeger", "Flea Market Access"):
            self.assertIn(name, pool)
        self.assertNotIn("Ground Zero Access", pool)
        self.assertNotIn("Prapor", pool)

    def test_shooting_cans_in_sphere_zero(self) -> None:
        self.assertTrue(self.can_reach_location("Prapor - Shooting Cans"))
        self.assertTrue(self.can_reach_location("Ground Zero - Extraction 1"))
        self.assertFalse(self.can_reach_location("Customs - Extraction 1"))

    def test_map_gated_quest(self) -> None:
        # Shootout Picnic is a Woods quest that comes after Background Check (Customs),
        # which itself follows the Ground Zero tutorial chain
        self.assertFalse(self.can_reach_location("Prapor - Shootout Picnic"))
        self.collect_by_name(["Woods Access"])
        self.assertFalse(self.can_reach_location("Prapor - Shootout Picnic"))
        self.collect_by_name(["Customs Access"])
        self.assertTrue(self.can_reach_location("Prapor - Shootout Picnic"))

    def test_trader_gated_quest(self) -> None:
        self.assertFalse(self.can_reach_location("Skier - Burning Rubber"))
        self.collect_by_name(["Skier"])
        self.assertTrue(self.can_reach_location("Skier - Burning Rubber"))

    def test_jaeger_needs_jaeger(self) -> None:
        jaeger_locations = [loc.name for loc in self.multiworld.get_locations(self.player)
                            if loc.name.startswith("Jaeger - ") and not loc.name.endswith("(Completed)")]
        self.assertTrue(jaeger_locations)
        state = self.multiworld.get_all_state()
        state.remove(self.world.create_item("Jaeger"))
        for name in jaeger_locations:
            self.assertFalse(self.multiworld.get_location(name, self.player).can_reach(state), name)

    def test_slot_data(self) -> None:
        data = self.world.fill_slot_data()
        self.assertEqual(data["goal"], "quest_count")
        self.assertEqual(data["goal_quest_count"], 60)
        self.assertEqual(data["starting_map"], "groundzero")
        self.assertEqual(data["starting_traders"], ["prapor", "therapist"])


class TestWeaponParts(TarkovTestBase):
    options = {"starting_map": "customs", "level_pacing": "fast"}

    def test_gunsmith_needs_flea_or_traders(self) -> None:
        name = "Mechanic - Gunsmith - Part 7"
        everything_but = ["Flea Market Access", "Skier", "Peacekeeper", "Jaeger"]
        state = self.multiworld.get_all_state()
        for item in everything_but:
            state.remove(self.world.create_item(item))
        location = self.multiworld.get_location(name, self.player)
        self.assertFalse(location.can_reach(state))
        state.collect(self.world.create_item("Flea Market Access"))
        self.assertTrue(location.can_reach(state))

    def test_first_gunsmith_needs_only_mechanic(self) -> None:
        # Gunsmith - Part 1 follows Saving the Mole, Mechanic's Ground Zero intro quest
        self.collect_by_name(["Mechanic"])
        self.assertFalse(self.can_reach_location("Mechanic - Gunsmith - Part 1"))
        self.collect_by_name(["Ground Zero Access"])
        self.assertTrue(self.can_reach_location("Mechanic - Gunsmith - Part 1"))


class TestLabKeycard(TarkovTestBase):
    options = {"starting_map": "customs", "lock_flea_market": True}

    def test_lab_extract_needs_keycard_source(self) -> None:
        location = self.multiworld.get_location("The Lab - Extraction 1", self.player)
        state = self.multiworld.get_all_state()
        for item in ("Mechanic", "Therapist", "Flea Market Access"):
            if state.has(item, self.player):
                state.remove(self.world.create_item(item))
        self.assertFalse(location.can_reach(state), "The Lab without any keycard source")
        state.collect(self.world.create_item("Mechanic"))
        self.assertTrue(location.can_reach(state), "Mechanic sells the keycard")
