`CultureInfoAttributeTests.CsvConfiguration_FromType_InvalidAttribute_ThrowsCultureNotFoundException` fails on Linux because ICU accepts `new CultureInfo("invalid")` instead of throwing like Windows NLS — expected, not a real bug.



# Testing guidance

The mutation tests take 200s, dont run them before you are ready to commit something. Only run once right before a commit, then fix your tests if the mutation score drops.


Csvhelper is made to efficient in memory use and fast in loaded data


# Design system

We have a design system in design-system/
Always use it when implementing UI