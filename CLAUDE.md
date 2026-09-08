# General guidance

Be efficient and fast when completing tasks. Never run complicated commands without a sensible timeout, for you execution time is not easy to track, but for me it can be minutes of delay.
If you expect a command to run for a long time decide if you can run a simpler command to still get to the same purpose.
For example run a reduced amount of tests if a test suite runs for a long time. And only run the full suite at a sensible point at the end (Or only in CI).
Try to efficiently use subagents when a task can be split up and done concurrently. But dont oversplit, especially when merge conflicts are likely.



# Testing guidance

`CultureInfoAttributeTests.CsvConfiguration_FromType_InvalidAttribute_ThrowsCultureNotFoundException` fails on Linux because ICU accepts `new CultureInfo("invalid")` instead of throwing like Windows NLS — expected, not a real bug.

The mutation tests take 200s, dont run them before you are ready to commit something. Only run once right before a commit, then fix your tests if the mutation score drops.


Csvhelper is made to efficient in memory use and fast in loaded data


# Design system

We have a design system in design-system/
Always use it when implementing UI