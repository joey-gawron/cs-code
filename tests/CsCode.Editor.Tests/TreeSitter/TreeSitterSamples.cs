namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>Representative code per evaluated language (the side-by-side screenshots and the colour-agreement measurement use them).</summary>
internal static class TreeSitterSamples
{
    public sealed record Sample(string Name, string FileName, string Grammar, string Text);

    public static readonly Sample[] All =
    [
        new("csharp", "Sample.cs", "c_sharp", """
            using System.Collections.Generic;
            using System.Threading.Tasks;

            namespace Demo.Text;

            /// <summary>Counts words in a document.</summary>
            [Serializable]
            public sealed class WordCounter<T>(IReadOnlyList<T> items) : IDisposable where T : notnull
            {
                private const int MaxWords = 10_000;
                private readonly Dictionary<string, int> _counts = new();

                public int this[string word] => _counts.TryGetValue(word, out var n) ? n : 0;

                public async Task<int> CountAsync(string text, CancellationToken ct = default)
                {
                    var total = 0;
                    foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (total++ > MaxWords) break; // stop early
                        _counts[word] = this[word] + 1;
                    }
                    await Task.Delay(1, ct).ConfigureAwait(false);
                    return items.Count + total + (int)3.5e2 + @"\n".Length + $"{total:X}".Length;
                }

                public void Dispose() => _counts.Clear();
            }
            """),
        new("typescript", "sample.ts", "typescript", """
            import { readFile } from "node:fs/promises";
            import type { Config } from "./config";

            export interface Options {
              readonly path: string;
              retries?: number;
            }

            enum Level { Debug = 0, Info, Warn }

            /** Loads and validates the configuration. */
            export async function load<T extends Config>(options: Options): Promise<T | undefined> {
              const text = await readFile(options.path, "utf8");
              let attempts = options.retries ?? 3;
              while (attempts-- > 0) {
                try {
                  const parsed = JSON.parse(text) as T;
                  return /^\d+$/.test(`${parsed.version}`) ? parsed : undefined;
                } catch (e: unknown) {
                  console.warn(`attempt failed: ${String(e)}`, Level.Warn);
                }
              }
              return undefined;
            }

            export class Cache<K, V> extends Map<K, V> {
              private hits = 0;
              get(key: K): V | undefined { this.hits++; return super.get(key); }
            }
            """),
        new("tsx", "Sample.tsx", "tsx", """
            import React, { useState } from "react";

            type Props = { title: string; items: string[]; onPick?: (item: string) => void };

            export function List({ title, items, onPick }: Props): JSX.Element {
              const [selected, setSelected] = useState<string | null>(null);
              return (
                <section className="list" aria-label={title}>
                  <h2>{title}</h2>
                  <ul>
                    {items.map((item) => (
                      <li key={item} className={item === selected ? "active" : ""}
                          onClick={() => { setSelected(item); onPick?.(item); }}>
                        {item.toUpperCase()}
                      </li>
                    ))}
                  </ul>
                  {selected && <p>Selected: <strong>{selected}</strong></p>}
                </section>
              );
            }
            """),
        new("rust", "sample.rs", "rust", """
            use std::collections::HashMap;
            use std::fmt;

            /// A word counter.
            #[derive(Debug, Default, Clone)]
            pub struct Counter<'a> {
                counts: HashMap<&'a str, usize>,
                total: u64,
            }

            impl<'a> Counter<'a> {
                pub const MAX: usize = 10_000;

                pub fn add(&mut self, text: &'a str) -> Result<usize, String> {
                    for word in text.split_whitespace() {
                        if self.counts.len() >= Self::MAX {
                            return Err(format!("too many words: {}", word));
                        }
                        *self.counts.entry(word).or_insert(0) += 1;
                        self.total += 1;
                    }
                    Ok(self.counts.len())
                }
            }

            impl fmt::Display for Counter<'_> {
                fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                    write!(f, "{} words, {} unique", self.total, self.counts.len()) // summary
                }
            }

            fn main() {
                let mut c = Counter::default();
                let n = c.add("a b a").unwrap_or(0);
                println!("{c} ({n}) {:?}", Some(3.5_f64));
            }
            """),
        new("python", "sample.py", "python", """"
            import asyncio
            from dataclasses import dataclass, field
            from typing import Iterable, Optional

            MAX_WORDS = 10_000


            @dataclass(frozen=True)
            class Counter:
                """Counts words in documents."""

                name: str
                counts: dict[str, int] = field(default_factory=dict)

                def add(self, text: str, *, limit: Optional[int] = None) -> int:
                    for word in text.split():
                        if limit is not None and len(self.counts) >= limit:
                            raise ValueError(f"too many words: {word!r}")
                        self.counts[word] = self.counts.get(word, 0) + 1
                    return len(self.counts)  # unique words

                @property
                def total(self) -> int:
                    return sum(self.counts.values())


            async def main(paths: Iterable[str]) -> None:
                c = Counter("demo")
                for p in paths:
                    with open(p, encoding="utf-8") as fh:
                        c.add(fh.read(), limit=MAX_WORDS)
                await asyncio.sleep(0.1)
                print(f"{c.name}: {c.total} words", True, None, 0x1F, r"\d+")


            if __name__ == "__main__":
                asyncio.run(main(["a.txt"]))
            """"),
        new("markdown", "README.md", "markdown", """
            # Word counter

            A **small** tool that counts _words_ in files. See [the docs](https://example.com/docs) or `count --help`.

            ## Usage

            1. Install it
            2. Run `count FILE`
               - [x] works on Linux
               - [ ] Windows

            > Note: large files are streamed.

            ```rust
            fn main() {
                let n = count("a b"); // 2
                println!("{n}");
            }
            ```

            ```python
            def count(text: str) -> int:
                return len(text.split())
            ```

            | Flag | Meaning |
            |------|---------|
            | `-v` | verbose |

            ---
            """),
        new("cpp", "sample.cpp", "cpp", """
            #include <map>
            #include <string>
            #define MAX_WORDS 10000

            namespace demo {

            /// Counts words.
            template <typename T>
            class Counter final : public Base {
            public:
                explicit Counter(std::string name) : name_(std::move(name)) {}
                virtual ~Counter() = default;

                [[nodiscard]] int add(const std::string& text) noexcept {
                    int added = 0;
                    for (auto it = text.begin(); it != text.end(); ++it) {
                        if (*it == ' ') { ++added; continue; } // separator
                        counts_[std::string(1, *it)] += 1;
                    }
                    return added > MAX_WORDS ? -1 : added + static_cast<int>(3.5f);
                }

            private:
                std::string name_;
                std::map<std::string, int> counts_{};
            };

            }  // namespace demo

            int main() {
                demo::Counter<int> c{"x"};
                return c.add(R"(a b)") == nullptr ? 0 : 1;
            }
            """),
        new("html", "index.html", "html", """
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>Word counter</title>
              <style>
                body { font-family: sans-serif; margin: 0 auto; max-width: 40em; }
                .count::after { content: " words"; color: #c00; }
                @media (max-width: 600px) { body { padding: 8px; } }
              </style>
            </head>
            <body>
              <!-- the counter -->
              <textarea id="text" rows="8" placeholder="Type here"></textarea>
              <p class="count" data-total="0">0</p>
              <script>
                const text = document.getElementById("text");
                text.addEventListener("input", (e) => {
                  const n = e.target.value.split(/\s+/).filter(Boolean).length;
                  document.querySelector(".count").textContent = `${n}`;
                });
              </script>
            </body>
            </html>
            """),
    ];
}
