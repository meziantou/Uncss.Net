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
                .WithCss()
                .WithLocaleBasedEncoding();


            Parallel.ForEach(args.Where(_ => !string.IsNullOrEmpty(_)), url =>
            {
                var browsingContext = BrowsingContext.New(config);
                browsingContext.Requesting += (sender, ev) => Console.WriteLine("Requesting: " + ((RequestEvent)ev).Request.Address);
                var u = Url.Create(url);
                if (u == null)
                {
                    return;
                }

                var openTask = browsingContext.OpenAsync(u);
                if(openTask == null)
                {
                    return;
                }

                var document = openTask.Result;
                var html = document.DocumentElement.InnerHtml;
                foreach (var stylesheet in document.StyleSheets.OfType<ICssStyleSheet>())
                {
                    foreach (var cssRule in stylesheet.Rules.OfType<ICssStyleRule>()) // TODO handle @ rule
                    {
                        foreach (var selector in GetAllSelectors(cssRule.Selector))
                        {
                            var r = new Rule(stylesheet.Href, selector.Text, cssRule.SelectorText);
                            r = rules.GetOrAdd(r, r);
                            if (r.Used)
                            {
                                continue;
                            }

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

            foreach (var rule in rules.Keys.Where(r => !r.Used).OrderBy(r => r.Url).ThenBy(r => r.Selector))
            {
                Console.WriteLine(rule.Url + ": " + rule.Selector);
            }

            File.WriteAllText("output.json", JsonConvert.SerializeObject(rules.Keys, Formatting.Indented));
        }

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
        public string Url { get; }
        public string Selector { get; }
        public string SelectorText { get; }
        public bool Used => UsageUrl != null;
        public string UsageUrl { get; set; }

        public Rule(string url, string selector, string selectorText)
        {
            Url = url ?? throw new ArgumentNullException(nameof(url));
            Selector = selector ?? throw new ArgumentNullException(nameof(selector));
            SelectorText = selectorText ?? throw new ArgumentNullException(nameof(selectorText));
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as Rule);
        }

        public bool Equals(Rule other)
        {
            return other != null &&
                   Url == other.Url &&
                   Selector == other.Selector;
        }

        public override int GetHashCode() => HashCode.Combine(Url, Selector);

        public static bool operator ==(Rule rule1, Rule rule2) => EqualityComparer<Rule>.Default.Equals(rule1, rule2);

        public static bool operator !=(Rule rule1, Rule rule2) => !(rule1 == rule2);
    }
}
