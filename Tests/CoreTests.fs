module Incremental.NET.Tests

open System
open Incremental.NET
open NUnit.Framework
open System.Threading.Tasks
open System.Threading

module Utilities =
    type TestNode  = { value: string; children: TestNode list }

    let mkTestNode (rng:Random) =
        let depth = rng.Next(1, 7)

        let rec loop (levels:int) : TestNode =
            let value = Char.ConvertFromUtf32(rng.Next(65, 90)).ToString()
            if levels = 0 then 
                { value = value; children = []}
            else
                let nchildren = rng.Next(1, 5)
                { value = value
                  children = [ for i = 0 to nchildren do yield loop (levels - 1) ] }

        loop depth  

open Utilities        

[<CoreTests>]
type CoreTests () =
    [<SetUp>]
    member this.Setup () =
        ()

    [<Test>]
    member __.``Basic computations with 1 signal work`` () =
        use Incr = new Incremental()
        let Var = Incr.Var

        let v1 = Var.Create(1)
        let s1 = Incremental.map Incr v1 ((+) 1)
        let _ = Incr.Stabilize()

        Assert.AreEqual(2, s1.Value)

    [<Test>]
    member __.``Basic computations with 2 signals work`` () =
        use Incr = new Incremental()
        let Var = Incr.Var

        let v1 = Var.Create(1)
        let s1 = Incremental.map Incr v1 ((+) 1)
        let s2 = Incremental.map2 Incr v1 s1 (fun a b -> a + b)
        let _ = Incr.Stabilize()

        Assert.AreEqual(2, s1.Value)
        Assert.AreEqual(3, s2.Value)

    [<Test>]
    member __.``Basic computations with 3 signals work`` () =
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

    [<Test>]
    member __.``Concurrency tests pass``() =
        let Incr = new Incremental()
        let Var = Incr.Var

        let myVar = Var.Create(1)
        let mySignal0 = Incremental.map Incr myVar id
        let mySignal1 = Incremental.map Incr mySignal0 ((+) 1)

        let _ =  Incr.Stabilize()

        let incrTask =
          Task.Factory.StartNew
            (fun () ->
              for i = 1 to 10000 do
                myVar.SetValue i)
    
        let ideal = ref 0
        let errored = ref 0
        let diffVersion = ref 0

        let readTasks =
          [| for _i = 0 to 3 do 
               yield Task.Factory.StartNew
                        (fun () ->
                          for j = 1 to 100000 do
                            Incr.Stabilize() |> ignore
                            //if j % 100 = 0 then Incr.Stabilize() |> ignore
                            let myVarVersionedV = myVar.VersionedValue
                            let myVarV = myVarVersionedV.Value

                            let mySignalVersionedV = mySignal1.VersionedValue
                            let mySignalV = mySignalVersionedV.Value

                            if myVarVersionedV.Version <> mySignalVersionedV.Version then
                                // Different world version (Another thread has called stabilize between our reads,
                                // so this is not an error)
                                Interlocked.Increment(diffVersion) |> ignore
                            elif myVarV + 1 = mySignalV then
                                // We got a consistent read, and a correct computation
                                Interlocked.Increment(ideal) |> ignore
                            else
                                // Anything else is crap
                                Interlocked.Increment(errored) |> ignore ) |]    
        incrTask.Wait()
        Task.WaitAll(readTasks)

        Assert.AreEqual(0, !errored)

    [<Test>]
    member __.``Getting the value of disposed signals throws`` () =
        use Incr = new Incremental()
        let Var = Incr.Var

        let v1 = Var.Create("a")
        v1.SetValue "b"
        let s1 = Incremental.map Incr v1 (fun x -> x + "b")
        s1.Dispose()

        // Assert.Throws does not work
        let mutable threw = false
        try
            s1.Value |> ignore
        with 
            :? ObjectDisposedException -> 
            threw <- true
        Assert.True(threw)

    [<Test>]
    member __.``Subscriptions start and stop firing as needed`` () =
        use Incr = new Incremental()
        let Var = Incr.Var

        let mutable observations = 0

        let v1 = Var.Create("a")
        let s1 = Incr.Map((+) "b", v1)
        using (v1.Subscribe(fun _ -> observations <- observations + 1)) <| fun _ ->
            v1.SetValue "b"
            Incr.Stabilize() |> ignore

        Assert.AreEqual(1, observations)

        using (s1.Subscribe(fun _ -> observations <- observations + 1)) <| fun _ ->
            v1.SetValue "c"
            Incr.Stabilize() |> ignore
        
        Assert.AreEqual(2, observations)

    [<Test>]
    member __.``Subscriptions report correct values`` () =
        use Incr = new Incremental()
        let Var = Incr.Var

        let mutable lastObservation = ""

        let v1 = Var.Create("a")
        using (v1.Subscribe(fun x -> lastObservation <- x)) <| fun _ ->
            v1.SetValue "b"
            Incr.Stabilize() |> ignore

        Assert.AreEqual("b", lastObservation)

        using (v1.Subscribe(fun x -> lastObservation <- x)) <| fun _ ->
            v1.SetValue "c"
            Incr.Stabilize() |> ignore
        
        Assert.AreEqual("c", lastObservation)

    [<Test>]
    member __.``Signals can be collected`` () =
        use Incr = new Incremental()
        let Var = Incr.Var

        let v1 = Var.Create("a")

        let createAndReadSignal() =
            let s1 = Incr.Map((+) "b", v1)
            Incr.Stabilize() |> ignore
            let _ = s1.Value
            s1.WeakRef
        
        for _i = 0 to 5 do
            let wsig = createAndReadSignal()
            GC.Collect()
            let alive, _ = wsig.TryGetTarget()
            Assert.False(alive, "Signal should be collected")

    [<Test>]
    member __.``Running random trees works``() =

        let rec runTree (node:TestNode) =
            let Incr = new Incremental()
            let root = Incr.Var.Create(node.value)
            for child in node.children do
                runNode child Incr root (node.value)

        and runNode (node:TestNode) (incrCtx:Incremental) (parent:ISignal<string>) (parentStr:string) =
            let signal = Incremental.map incrCtx parent (fun s -> s + node.value)
            incrCtx.Stabilize() |> ignore
            Assert.AreEqual(parentStr + node.value, signal.Value)

            for child in node.children do
                runNode child incrCtx signal (parentStr + node.value)
            
            incrCtx.Stabilize() |> ignore
            Assert.AreEqual(parentStr + node.value, signal.Value)

        let rng = new Random(1)
        for _i = 0 to 2 do
            let node = mkTestNode rng
            printfn "Running tree %A" node
            runTree node

