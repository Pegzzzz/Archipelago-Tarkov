from dataclasses import dataclass

from Options import Choice, DeathLink, DefaultOnToggle, OptionSet, PerGameCommonOptions, Range, Toggle


class Goal(Choice):
    """What you need to do to finish your game.

    quest_count: complete a number of quests that are checks (see Goal Quest Count).
    collector: complete Fence's "Collector" quest (the Kappa container).
    all_extracts: survive and extract at least once from every map."""
    display_name = "Goal"
    option_quest_count = 0
    option_collector = 1
    option_all_extracts = 2
    default = 0


class GoalQuestCount(Range):
    """How many check quests must be completed for the Quest Count goal.
    If fewer quest checks exist with your other settings, the goal uses all of them."""
    display_name = "Goal Quest Count"
    range_start = 5
    range_end = 372
    default = 60


class StartingMap(Choice):
    """The map you can raid on from the start. Every other map is an item.
    Ground Zero matches the vanilla new-player experience; most early Prapor quests start there.
    The Lab is not offered because it needs a keycard to enter."""
    display_name = "Starting Map"
    option_ground_zero = 0
    option_factory = 1
    option_customs = 2
    option_woods = 3
    option_interchange = 4
    option_shoreline = 5
    option_reserve = 6
    option_lighthouse = 7
    option_streets_of_tarkov = 8
    default = 0


class StartingTraders(Choice):
    """Which traders are available from the start. Every other main trader is an item.

    prapor_and_therapist: the two traders new players lean on the most.
    random_two: two random traders out of Prapor, Therapist, Skier, Peacekeeper, Mechanic, Ragman and Jaeger.
    random_one: one random trader."""
    display_name = "Starting Traders"
    option_prapor_and_therapist = 0
    option_random_two = 1
    option_random_one = 2
    default = 0


class QuestTraders(OptionSet):
    """Traders whose quests are checks. Quests from traders left out still work as normal in game,
    they just don't send anything."""
    display_name = "Quest Traders"
    valid_keys = frozenset({"Prapor", "Therapist", "Skier", "Peacekeeper", "Mechanic", "Ragman", "Jaeger"})
    default = frozenset({"Prapor", "Therapist", "Skier", "Peacekeeper", "Mechanic", "Ragman", "Jaeger"})


class MaxQuestLevel(Range):
    """Quests that need a higher player level than this to unlock are not checks.
    Lower values make shorter games."""
    display_name = "Max Quest Level"
    range_start = 1
    range_end = 79
    default = 79


class HideoutChecks(DefaultOnToggle):
    """Building each hideout area level is a check."""
    display_name = "Hideout Checks"


class ExtractsPerMap(Range):
    """How many successful PMC extractions on each map are checks.
    For example 2 means the first and second time you survive on Customs both send a check."""
    display_name = "Extracts Per Map"
    range_start = 0
    range_end = 5
    default = 1


class LockFleaMarket(DefaultOnToggle):
    """The Flea Market stays banned until you find "Flea Market Access"."""
    display_name = "Lock Flea Market"


class LevelPacing(Choice):
    """How the logic estimates the player level you can reach.
    Logic assumes you need more maps unlocked before high level quests and loyalty levels are expected of you.

    relaxed: one extra map is expected for every 8 player levels.
    normal: one extra map for every 10 levels.
    fast: one extra map for every 15 levels."""
    display_name = "Level Pacing"
    option_relaxed = 8
    option_normal = 10
    option_fast = 15
    default = 10


class TrapPercentage(Range):
    """Percentage of filler items replaced by traps (Fence Grudge, Exhaustion, Scav Cooldown)."""
    display_name = "Trap Percentage"
    range_start = 0
    range_end = 50
    default = 10


class StartingKit(Choice):
    """Give your PMC a random starting kit, rolled from the seed: a weapon with magazines and matching ammo,
    body armor, a rig, a backpack, meds, food and money (see Starting Kit Quality).

    off: the normal starting stash of your SPT profile's edition.
    extra: the kit arrives as a message on top of the normal starting stash.
    replace: your starting stash and gear are removed and the kit is all you start with (the knife, pockets and
    secure container stay). Only applies to a brand-new profile; an older profile gets the kit on top instead."""
    display_name = "Starting Kit"
    option_off = 0
    option_extra = 1
    option_replace = 2
    default = 0


class StartingKitQuality(Choice):
    """How good the random starting kit is.

    scrappy: a cheap gun, light armor, little money.
    standard: a mid-range gun, class 3 armor and a helmet now and then.
    geared: a good gun with better ammo, class 4 armor, a helmet and more money."""
    display_name = "Starting Kit Quality"
    option_scrappy = 0
    option_standard = 1
    option_geared = 2
    default = 1


class TarkovDeathLink(DeathLink):
    """When your PMC dies in raid, everyone with DeathLink dies too, and the reverse.
    Receiving a death while in raid needs the optional client plugin; it is ignored outside raids."""


@dataclass
class TarkovOptions(PerGameCommonOptions):
    goal: Goal
    goal_quest_count: GoalQuestCount
    starting_map: StartingMap
    starting_traders: StartingTraders
    quest_traders: QuestTraders
    max_quest_level: MaxQuestLevel
    hideout_checks: HideoutChecks
    extracts_per_map: ExtractsPerMap
    lock_flea_market: LockFleaMarket
    level_pacing: LevelPacing
    trap_percentage: TrapPercentage
    starting_kit: StartingKit
    starting_kit_quality: StartingKitQuality
    death_link: TarkovDeathLink
