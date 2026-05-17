using VirtualDesktop;

if (args.Length == 0)
{
    int current = Desktop.FromDesktop(Desktop.Current);
    int count = Desktop.Count;
    Console.WriteLine($"{current}/{count}");
    return 0;
}

switch (args[0])
{
    case "--current":
        Console.WriteLine(Desktop.FromDesktop(Desktop.Current));
        return 0;
    case "--count":
        Console.WriteLine(Desktop.Count);
        return 0;
}

if (!int.TryParse(args[0], out int desktopNumber))
{
    Console.Error.WriteLine($"Invalid desktop number: {args[0]}");
    return 1;
}

if (desktopNumber < 0 || desktopNumber >= Desktop.Count)
{
    Console.Error.WriteLine($"Desktop number out of range: {desktopNumber} (0-{Desktop.Count - 1})");
    return 1;
}

Desktop.FromIndex(desktopNumber).MakeVisible();
return 0;
