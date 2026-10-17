// "What to look for": a calm grid of berries, a sheet per berry, and the profile sheet behind the avatar.
public partial class BerryfinderApp
{
    private readonly ClientReactive<string> _berriesFilter = new("season");

    private void RenderBerriesPage(UIView view)
    {
        var today = LocalToday();
        var showAll = _berriesFilter.Value == "all";
        var berries = BerryCatalog.All.Where(b => showAll || BerryCatalog.InSeason(b, today)).ToList();

        view.ScrollArea(["flex-1 min-h-0"], content: scroll =>
            scroll.Column(["w-full max-w-3xl mx-auto px-5 pt-3 pb-10 gap-6", Design.Appear], content: page =>
            {
                page.Column(["gap-2 pt-2"], content: head =>
                {
                    head.Text([Design.Overline, "text-muted-foreground"], text: T("What to look for", "Mitä etsiä"));
                    head.Text([Design.LargeTitle], text: T("Berries", "Marjat"));
                });

                page.ToggleGroupSingle(["grid grid-cols-2 gap-1 bg-muted rounded-full p-1 self-start w-64"], value: _berriesFilter.Value, ariaLabel: T("Filter", "Suodatin"),
                    onValueChange: async v => { if (v.Length > 0) _berriesFilter.Value = v; }, content: g =>
                    {
                        foreach (var (id, en, fi) in new[] { ("season", "In season", "Sesongissa"), ("all", "All", "Kaikki") })
                        {
                            g.ToggleGroupItem(["h-9 rounded-full text-[15px] font-medium text-muted-foreground data-[state=on]:bg-card data-[state=on]:text-foreground data-[state=on]:shadow-sm"],
                                value: id, content: i => i.Text(text: T(en, fi)));
                        }
                    });

                if (berries.Count == 0)
                {
                    page.EmptyState(T("Nothing is in season right now", "Mikään ei ole nyt sesongissa"), icon: "snowflake",
                        description: T("Switch to All to see every berry and when it ripens.", "Valitse Kaikki nähdäksesi jokaisen marjan ja milloin se kypsyy."));
                    return;
                }

                page.Box(["grid grid-cols-2 md:grid-cols-3 gap-3"], content: grid =>
                {
                    foreach (var berry in berries)
                    {
                        var id = berry.Id;
                        grid.Button([$"relative overflow-hidden rounded-[24px] h-48 p-4 text-left flex flex-col justify-end border-0 bg-[{berry.Color}] shadow-[0_8px_24px_rgba(0,0,0,0.12)]", Design.Press], key: id,
                            ariaLabel: HasSpots(berry.Id) ? $"{berry.Name(_language.Value)}, {T("spots on the map", "paikkoja kartalla")}" : berry.Name(_language.Value),
                            onClick: async () => _berrySheet.Value = id, content: tile =>
                            {
                                tile.Image(["absolute right-2 top-2 w-28 h-28"], src: berry.Art, alt: "");
                                if (HasSpots(berry.Id))
                                {
                                    // A small pin, clear of the illustration: this berry has spots on the map
                                    tile.Box(["absolute left-3 top-3 w-7 h-7 rounded-full bg-white/20 flex items-center justify-center"], content: b =>
                                        b.Icon([Icon.Sm, "text-white"], name: "map-pin"));
                                }

                                tile.Text(["text-[20px] leading-tight font-bold tracking-tight text-white"], text: berry.Name(_language.Value));
                                tile.Text(["text-[13px] text-white/75"], text: BerryCatalog.SeasonText(berry, _language.Value));
                            });
                    }
                });

                page.Text([Design.Caption], text: T("A pin means we can show you spots on the map. Season dates are typical for northern Finland and shift with the weather. Only pick and eat berries you can identify with certainty.",
                    "Nasta tarkoittaa, että voimme näyttää paikkoja kartalla. Sesonkiajat ovat tyypillisiä Pohjois-Suomessa ja vaihtelevat sään mukaan. Poimi ja syö vain marjoja, jotka tunnistat varmasti."));
            }));
    }

    // The berry sheet: big picture, three short answers, one safe-use line
    private void RenderBerrySheet(UIView view)
    {
        if (_berrySheet.Value is not { } id || !BerryCatalog.TryGet(id, out var berry))
        {
            return;
        }

        var lang = _language.Value;
        view.Drawer([Drawer.Content, "bg-background max-h-[92vh] md:max-w-xl md:mx-auto"], open: true, showHandle: true,
            onOpenChange: async open => { if (!open) _berrySheet.Value = null; }, content: sheet =>
                sheet.ScrollArea(["max-h-[80vh]"], content: scroll =>
                    scroll.Column(["px-5 pb-10 pt-2 gap-6"], content: c =>
                    {
                        c.Box([$"h-52 rounded-[28px] flex items-center justify-center bg-[{berry.Color}]"], content: b => b.Image(["w-40 h-40"], src: berry.Art, alt: berry.Name(lang)));

                        c.Column(["gap-1.5"], content: head =>
                        {
                            head.Text(["text-[30px] leading-[1.1] font-bold tracking-tight text-foreground"], text: berry.Name(lang));
                            head.Text([Design.Subhead, "italic"], text: berry.Latin);
                            head.Row(["items-center gap-2 pt-1"], content: r =>
                            {
                                r.Icon([Icon.Sm, "text-muted-foreground"], name: "calendar");
                                r.Text([Design.Subhead], text: BerryCatalog.SeasonText(berry, lang));
                            });
                        });

                        foreach (var (label, text) in new[]
                        {
                            (T("What it looks like", "Miltä se näyttää"), berry.Look(lang)),
                            (T("Where to find it", "Mistä sen löytää"), berry.Where(lang)),
                            (T("Good to know", "Hyvä tietää"), berry.Tip(lang)),
                        })
                        {
                            c.Column(["gap-1.5"], key: label, content: s =>
                            {
                                s.Text([Design.Overline, "text-muted-foreground"], text: label);
                                s.Text([Design.Body], text: text);
                            });
                        }

                        c.Row([Design.Sheet, "p-4 gap-3 items-start"], content: r =>
                        {
                            r.Icon([Icon.Md, "text-muted-foreground shrink-0 mt-0.5"], name: "shield-check");
                            r.Text([Design.Subhead], text: T("Only pick and eat berries you can identify with certainty. Take only what you need and leave some for the forest.",
                                "Poimi ja syö vain marjoja, jotka tunnistat varmasti. Ota vain tarpeeseen ja jätä osa metsälle."));
                        });

                        if (HasSpots(berry.Id))
                        {
                            c.Button([Button.PrimaryLg, "w-full h-14 rounded-full text-[17px] font-semibold gap-2", Design.Press], onClick: async () =>
                            {
                                _species.Value = berry.Id;
                                _berrySheet.Value = null;
                                await GoAsync("/map");
                            }, content: b =>
                            {
                                b.Icon([Icon.Md], name: "map");
                                b.Text(text: T("Show spots on the map", "Näytä paikat kartalla"));
                            });
                        }
                        else
                        {
                            c.Button([Button.OutlineLg, "w-full h-14 rounded-full text-[17px] font-semibold"], disabled: true, text: T("Spots coming soon", "Paikat tulossa pian"));
                            c.Text([Design.Caption, "-mt-3 text-center"], text: T("We only show spots where our maps or model are reliable enough, and this berry isn't there yet.",
                                "Näytämme paikkoja vain, kun karttamme tai mallimme on riittävän luotettava, eikä tämä marja ole vielä siellä."));
                        }
                    })));
    }

    // The avatar's sheet: everything that is not the daily question, grouped like Apple's Settings
    private void RenderProfileSheet(UIView view)
    {
        if (!_profileOpen.Value)
        {
            return;
        }

        view.Drawer([Drawer.Content, "bg-background max-h-[92vh] md:max-w-xl md:mx-auto"], open: true, showHandle: true,
            onOpenChange: async open => _profileOpen.Value = open, content: sheet =>
                sheet.ScrollArea(["max-h-[84vh]"], content: scroll =>
                    scroll.Column(["px-5 pb-10 pt-2 gap-5"], content: c =>
                    {
                        c.Text(["text-[30px] leading-[1.1] font-bold tracking-tight text-foreground"], text: T("You", "Sinä"));

                        if (!_isSignedIn.Value)
                        {
                            c.Button([Button.PrimaryLg, "w-full h-14 rounded-full text-[17px] font-semibold", Design.Press], text: T("Sign in", "Kirjaudu"),
                                onClick: async () => { _profileOpen.Value = false; await ClientFunctions.LoginShowAsync(T("Sign in to save places and send check-ins", "Kirjaudu tallentaaksesi paikkoja ja lähettääksesi havaintoja")); });
                            c.Text([Design.Caption, "-mt-3"], text: T("You can use BerryFinder without an account.", "BerryFinderia voi käyttää ilman tiliä."));
                        }

                        ProfileGroup(c, [
                            ("bookmark", "#0A84FF", T("Saved places", "Tallennetut paikat"), "/saved"),
                            ("route", "#FF9F0A", T("Plan a trip", "Suunnittele retki"), "/plan"),
                            ("clipboard-pen", "#30B05A", T("Field report", "Maastohavainto"), "/field"),
                        ]);

                        c.Row([Design.Sheet, "items-center justify-between px-4 h-14"], content: r =>
                        {
                            r.Row(["items-center gap-3"], content: l =>
                            {
                                l.Box(["w-8 h-8 rounded-[9px] flex items-center justify-center bg-[#8E8E93]"], content: b => b.Icon([Icon.Sm, "text-white"], name: "languages"));
                                l.Text([Design.Body], text: T("Language", "Kieli"));
                            });
                            r.Select(["w-32"], options: Languages, value: _language.Value, ariaLabel: T("Language", "Kieli"), onValueChange: async v => _language.Value = v);
                        });

                        var info = new List<(string, string, string, string)>
                        {
                            ("user-round", "#5E5CE6", T("Account and privacy", "Tili ja yksityisyys"), "/account"),
                            ("sparkles", "#BF5AF2", T("How it works", "Miten se toimii"), "/methodology"),
                            ("leaf", "#30B05A", T("Responsible picking", "Vastuullinen marjastus"), "/responsible"),
                            ("credit-card", "#FF375F", T("Plans", "Tilaukset"), "/pricing"),
                            ("message-circle", "#0A84FF", T("Contact", "Yhteystiedot"), "/contact"),
                            ("info", "#8E8E93", T("About BerryFinder", "Tietoa BerryFinderista"), "/about"),
                        };
                        if (_isAdmin.Value)
                        {
                            info.Add(("shield", "#1C1C1E", "Admin", "/admin"));
                        }

                        ProfileGroup(c, info);
                    })));
    }

    private void ProfileGroup(UIView view, IReadOnlyList<(string Icon, string Tint, string Title, string Path)> rows)
    {
        view.Column([Design.Sheet, "overflow-hidden"], content: group =>
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var (icon, tint, title, path) = rows[i];
                if (i > 0)
                {
                    group.Separator(["default", "ml-[60px]"]);
                }

                group.Button(["w-full flex items-center gap-3 px-4 h-14 bg-transparent border-0 text-left", Design.Press], key: path,
                    onClick: async () => { _profileOpen.Value = false; await GoAsync(path); }, content: row =>
                    {
                        row.Box([$"w-8 h-8 rounded-[9px] flex items-center justify-center bg-[{tint}]"], content: b => b.Icon([Icon.Sm, "text-white"], name: icon));
                        row.Text([Design.Body, "flex-1"], text: title);
                        row.Icon([Icon.Sm, "text-muted-foreground/60"], name: "chevron-right");
                    });
            }
        });
    }
}
