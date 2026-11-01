# RestaurantOrders — navigation buttons update

This package contains the source project with the navigation-link styling fix.

## What changed
- Links such as **← Αρχική**, **← Πίσω**, and **← Πίσω στα τραπέζια** now use the same rounded, filled button treatment across the app.
- The links in the top navigation bar are styled as buttons too.
- The stylesheet URL is versioned so the browser is less likely to reuse a stale cached stylesheet.
- Existing custom colors for category tiles, product tiles, page backgrounds, and the four order-action buttons are preserved.
- Build folders (`bin` and `obj`) are intentionally omitted to prevent launching a stale compiled build. Visual Studio will regenerate them.

## Run it
1. Extract this ZIP to a new folder.
2. Open `RestaurantOrders.sln` (or `RestaurantOrders.csproj`) in Visual Studio.
3. Let Visual Studio restore NuGet packages.
4. Build the solution, then run this project.
5. In Chrome, use **Ctrl+F5** to hard-refresh the page. Make sure the old app instance is stopped and that you launched the project from this extracted folder.

The project database file is retained unchanged.
