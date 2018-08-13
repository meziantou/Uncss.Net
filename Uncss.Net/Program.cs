using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom.Css;
using AngleSharp.Dom.Events;
using Newtonsoft.Json;

namespace Uncss.Net
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            var rules = new ConcurrentDictionary<Rule, Rule>();

            var config = Configuration.Default
                .WithDefaultLoader(conf =>
                {
                    // Download page resources such as CSS files
                    conf.IsResourceLoadingEnabled = true;
                    conf.IsNavigationEnabled = true;
                    conf.Filter = request =>
                    {
                        var path = request.Address.Path;
                        if (!path.Contains("."))
                        {
                            return true;
                        }

                        if (path.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

                        return false;
                    };
                })
                .WithCss() // Parse CSS files and compute the style of every elements
                .WithLocaleBasedEncoding();

            // Process the urls set in the command line
            Parallel.ForEach(args.Where(_ => !string.IsNullOrEmpty(_)), url =>
            {
                // Create a new BrowsingContext to download and parse the page
                var browsingContext = BrowsingContext.New(config);
                browsingContext.Requesting += (sender, ev) => Console.WriteLine("Requesting: " + ((RequestEvent)ev).Request.Address);
                var u = Url.Create(url);
                if (u == null)
                {
                    return;
                }

                // Open the page
                var openTask = browsingContext.OpenAsync(u);
                if (openTask == null)
                {
                    return;
                }

                var document = openTask.Result;

                // Get all stylesheets of the document
                foreach (var stylesheet in document.StyleSheets.OfType<ICssStyleSheet>())
                {
                    foreach (var cssRule in stylesheet.Rules.OfType<ICssStyleRule>()) // TODO handle @ rule and remove ::before, ::after, :hover, ...
                    {
                        foreach (var selector in GetAllSelectors(cssRule.Selector))
                        {
                            var r = new Rule(stylesheet.Href, selector.Text, cssRule.SelectorText);
                            r = rules.GetOrAdd(r, r);
                            if (r.Used)
                            {
                                continue;
                            }

                            // Check if the rule match an element of the document
                            var match = document.QuerySelector(r.Selector);
                            if (match != null)
                            {
                                lock (r)
                                {
                                    if (!r.Used)
                                    {
                                        r.UsageUrl = url;
                                    }
                                }

                                break;
                            }
                        }
                    }
                }
            });

            // Display the list of unused rules
            foreach (var rule in rules.Keys.Where(r => !r.Used).OrderBy(r => r.StylesheetUrl).ThenBy(r => r.Selector))
            {
                Console.WriteLine(rule.StylesheetUrl + ": " + rule.Selector);
            }

            File.WriteAllText("output.json", JsonConvert.SerializeObject(rules.Keys, Formatting.Indented));
        }


        // Get all selectors of a CSS rule
        // ".a, .b, .c" contains 3 selectors ".a", ".b" and ".c"
        private static IEnumerable<ISelector> GetAllSelectors(ISelector selector)
        {
            if (selector is IEnumerable<ISelector> selectors)
            {
                foreach (var innerSelector in selectors)
                {
                    foreach (var s in GetAllSelectors(innerSelector))
                    {
                        yield return s;
                    }
                }
            }
            else
            {
                yield return selector;
            }
        }
    }

    internal class Rule : IEquatable<Rule>
    {
        public string StylesheetUrl { get; }
        public string Selector { get; }
        public string RuleText { get; }
        public string UsageUrl { get; set; }
        public bool Used => UsageUrl != null;

        public Rule(string stylesheetUrl, string selector, string ruleText)
        {
            StylesheetUrl = stylesheetUrl ?? throw new ArgumentNullException(nameof(stylesheetUrl));
            Selector = selector ?? throw new ArgumentNullException(nameof(selector));
            RuleText = ruleText ?? throw new ArgumentNullException(nameof(ruleText));
        }

        public override bool Equals(object obj) => Equals(obj as Rule);

        public bool Equals(Rule other) => other != null && StylesheetUrl == other.StylesheetUrl && Selector == other.Selector;

        public override int GetHashCode() => HashCode.Combine(StylesheetUrl, Selector);

        public static bool operator ==(Rule rule1, Rule rule2) => EqualityComparer<Rule>.Default.Equals(rule1, rule2);

        public static bool operator !=(Rule rule1, Rule rule2) => !(rule1 == rule2);
    }
}
