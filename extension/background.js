// Bouton de la barre d'outils (et Ctrl+Alt+R) : ouvre l'onglet de recherche, ou le ramène au premier plan.
const PAGE = messenger.runtime.getURL("search.html");

messenger.action.onClicked.addListener(async () => {
  const tabs = await messenger.tabs.query({});
  const existing = tabs.find(t => t.url && t.url.startsWith(PAGE));
  if (existing) {
    await messenger.tabs.update(existing.id, { active: true });
    if (existing.windowId != null) await messenger.windows.update(existing.windowId, { focused: true });
  } else {
    await messenger.tabs.create({ url: PAGE });
  }
});
