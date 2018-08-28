using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom.Css;
using AngleSharp.Dom.Events;
using AngleSharp.Extensions;
using AngleSharp.Parser.Css;
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

                        if (path.EndsWith("prism.min.css", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
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
            Parallel.ForEach(args.Where(_ => !string.IsNullOrEmpty(_)), new ParallelOptions { MaxDegreeOfParallelism = -1 }, url =>
            {
                try
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
                        foreach (var cssRule in GetAllRules(stylesheet))
                        {
                            foreach (var selector in GetAllSelectors(cssRule.Selector))
                            {
                                var querySelector = GetSelector(selector);
                                var r = new Rule(stylesheet.Href, selector.Text, querySelector.Text, cssRule.SelectorText);
                                r = rules.GetOrAdd(r, r);
                                if (r.Used)
                                {
                                    continue;
                                }

                                // Check if the rule match an element of the document
                                var match = document.QuerySelector(r.QuerySelector);
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
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            });

            // Display the list of unused rules
            foreach (var rule in rules.Keys.Where(r => !r.Used).OrderBy(r => r.StylesheetUrl).ThenBy(r => r.Selector))
            {
                Console.WriteLine((rule.StylesheetUrl ?? "inline") + ": " + rule.Selector);
            }

            File.WriteAllText("output.json", JsonConvert.SerializeObject(rules.Keys, Formatting.Indented));
        }

        private static IEnumerable<ICssStyleRule> GetAllRules(ICssNode node)
        {
            if (node is ICssStyleRule a)
            {
                yield return a;
            }

            foreach (var child in node.Children)
            {
                foreach (var item in GetAllRules(child))
                {
                    yield return item;
                }
            }
        }

        // Get all selectors of a CSS rule
        // ".a, .b, .c" contains 3 selectors ".a", ".b" and ".c"
        private static IEnumerable<ISelector> GetAllSelectors(ISelector selector)
        {
            if (selector.GetType().Name == "ListSelector")
            {
                foreach (var s in ((IEnumerable<ISelector>)selector))
                {
                    foreach (var a in GetAllSelectors(s))
                    {
                        yield return a;
                    }
                }
            }

            yield return selector;
            yield break;
        }

        private static ISelector GetSelector(ISelector selector)
        {
            var text = selector.ToCss()
                .Replace("::after", "")
                .Replace(":after", "")
                .Replace("::before", "")
                .Replace(":before", "")
                .Replace(":active", "")
                .Replace(":focus", "")
                .Replace(":hover", "");

            return new CssParser().ParseSelector(text);
        }
    }

    internal class Rule : IEquatable<Rule>
    {
        public string StylesheetUrl { get; }
        public string Selector { get; }
        public string QuerySelector { get; }
        public string RuleText { get; }
        public string UsageUrl { get; set; }
        public bool Used => UsageUrl != null;

        public Rule(string stylesheetUrl, string selector, string querySelector, string ruleText)
        {
            StylesheetUrl = stylesheetUrl;
            Selector = selector ?? throw new ArgumentNullException(nameof(selector));
            QuerySelector = querySelector ?? throw new ArgumentNullException(nameof(querySelector));
            RuleText = ruleText ?? throw new ArgumentNullException(nameof(ruleText));
        }

        public override bool Equals(object obj) => Equals(obj as Rule);

        public bool Equals(Rule other) => other != null && StylesheetUrl == other.StylesheetUrl && Selector == other.Selector;

        public override int GetHashCode() => HashCode.Combine(StylesheetUrl, Selector);

        public static bool operator ==(Rule rule1, Rule rule2) => EqualityComparer<Rule>.Default.Equals(rule1, rule2);

        public static bool operator !=(Rule rule1, Rule rule2) => !(rule1 == rule2);
    }
}
