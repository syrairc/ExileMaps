# ExileMaps

An ExileCore2 overlay plugin for the endgame Atlas. It scores every map on the Atlas from weights
you set, styles the nodes and labels to match, draws the connections between them, and manages
waypoints and multi-stop tours with real routing behind them.

## Donations

This plugin would not be possible without the hard work of the ExileCore/ExileAPI developers. If you want to support plugin development, donate to them. See below for donation information.

ExileAPI: https://github.com/exApiTools/ExileApi-Compiled

ExileCore2: https://github.com/exCore2/ExileCore2

## Credits

Weight-aware routing for tours and waypoints (the `Weight-aware routing` / `Extra map cost` controls, and the Dijkstra route cost that backs them) is sTafnio's work, from pull request #33. The refactor moved the code into `Cache.cs` / `Features.cs`, but the routing model is theirs.

## Forks

You may fork and re-release as you please.

## Pull Requests

If you submit a pull request, please explain what problem it is solving.

If you are contributing UI elements and used AI for code, please ensure it does not use non-ascii characters (em-dash, middle dot, curly quotes) that do not render with imgui.

## AI usage disclosure

GitHub Copilot was used for various aspects of this project, including code generation, documentation, and troubleshooting.

Claude was used at one point and turned this into 90 fucking files for no reason???
