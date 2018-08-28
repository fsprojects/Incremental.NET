# Incremental.NET

A library for incremental computations.

Based on [janestreet/incremental](https://github.com/janestreet/incremental) for OCaml. Also inspired by [Gjallarhorn](https://github.com/ReedCopsey/Gjallarhorn).

## Usage -- see [tests](https://www.github.com/isaksky/Incremental.NET/blob/master/Tests/CoreTests.fs)

```fsharp
use Incr = new Incremental()
let Var = Incr.Var

let myOrders = Var.Create([100; 150; 200])
use maxOrder = Incremental.map Incr myOrders List.max
use minOrder = Incremental.map Incr myOrders List.min
use orderRange = Incremental.map2 Incr maxOrder minOrder (-)

let log = ResizeArray<string>()
use maxChanged = maxOrder.Subscribe (fun maxOrder -> log.Add(sprintf "Max changed to %d" maxOrder))

let worldVersion = Incr.Stabilize()

Assert.AreEqual([100; 150; 200], myOrders.Value)
Assert.AreEqual(200, maxOrder.Value)
Assert.AreEqual(100, minOrder.Value)
Assert.AreEqual(100, orderRange.Value)

myOrders.SetValue([300;400;500])
Incr.Stabilize() |> ignore

Assert.AreEqual(500, maxOrder.Value)

Assert.AreEqual("Max changed to 500", Seq.last(log))
```

## Differences from Gjallarhorn:

- Thread safety (easy for user to make sure they get a consistent view)
- Control when computations happen (user must call .Stabilize())
- Fewer features, less code

## Roadmap

- More tests
- Nuget release
- [Incremental.Bind](https://youtu.be/G6a5G5i4gQU?t=8m35s) support

## See also

- [Gjallarhorn](https://github.com/ReedCopsey/Gjallarhorn) for F#.
- https://opensource.janestreet.com/incremental/