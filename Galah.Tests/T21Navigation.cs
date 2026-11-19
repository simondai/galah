using Galah.Services;

namespace Galah.Tests;

/// <summary>T21 - category navigation maths (Sprint 3 / E4): wrapping and digit addressing.</summary>
internal static class T21_Navigation
{
    public static void Run()
    {
        Program.Section("category navigation maths");

        Program.Check("forward wraps at the end", TabNavigation.StepIndex(3, 2, +1) == 0,
            TabNavigation.StepIndex(3, 2, +1).ToString());
        Program.Check("backward wraps at the start", TabNavigation.StepIndex(3, 0, -1) == 2,
            TabNavigation.StepIndex(3, 0, -1).ToString());
        Program.Check("middle steps normally",
            TabNavigation.StepIndex(4, 1, +1) == 2 && TabNavigation.StepIndex(4, 1, -1) == 0);
        Program.Check("single category cannot switch", TabNavigation.StepIndex(1, 0, +1) == -1);
        Program.Check("empty category list cannot switch", TabNavigation.StepIndex(0, -1, +1) == -1);
        Program.Check("no selection starts at the first", TabNavigation.StepIndex(3, -1, +1) == 0);

        Program.Check("digit 1 addresses the first category", TabNavigation.DigitToIndex(1) == 0);
        Program.Check("digit 9 addresses the ninth", TabNavigation.DigitToIndex(9) == 8);
        Program.Check("digit 0 addresses nothing", TabNavigation.DigitToIndex(0) == -1);
        Program.Check("out-of-range digits ignored",
            TabNavigation.DigitToIndex(-1) == -1 && TabNavigation.DigitToIndex(10) == -1);
    }
}
