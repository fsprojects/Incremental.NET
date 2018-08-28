namespace rec Incremental.NET

open System
open System.Collections
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

type IncrementalOptions() = class end

type IVersionedValue =
    abstract member BoxedValue : obj
    abstract member Version: int
    abstract member Make: value:obj -> version:int ref -> IVersionedValue

type IVersionedValue<'t> =
    inherit IVersionedValue
    abstract member Value : 't

type private VersionedValue<'t>(value:'t, version:int ref) =
    let mutable _value = value
    let mutable _version = version
    member __.Version = _version
    member __.Value = _value
    member __.UnsafeSetValue(value:'t) = _value <- value

    interface IVersionedValue<'t> with
        member __.Value = value
        member __.Version = !_version
        member __.BoxedValue = _value :> obj
        member __.Make (value:obj) (version:int ref) =
            upcast VersionedValue(value :?> 't, version)

type ISignalInternal =
    inherit IDisposable    
    abstract member IsDisposed: bool
    abstract member BoxedVersionedValue: IVersionedValue
    abstract member UnsafeSetValue: IVersionedValue -> unit
    abstract member WeakRef: WeakReference<ISignalInternal>
    abstract member Compute: arg1:obj * arg2:obj -> obj

type ISignal<'o> =
    inherit ISignalInternal
    inherit IObservable<'o>
    abstract member Value : 'o
    abstract member VersionedValue: IVersionedValue<'o>
    
type IMutable<'v> =
    inherit ISignal<'v>
    abstract member Value : 'v with get
    abstract member SetValue : 'v -> unit

module Utils =
    /// Note: Not thread safe
    type WeakList<'t when 't: not struct>() =
        let list1 = ResizeArray<WeakReference<'t>>()
        let list2 = ResizeArray<WeakReference<'t>>()
        let mutable usingList1 = true

        member private __.list = if usingList1 then list1 else list2

        member this.Add(item: WeakReference<'t>) = this.list.Add(item)

        member this.Add(item: 't) = this.list.Add(WeakReference<'t>(item))
        
        member private __.Collect() : unit =
            let listNew, listOld = 
                if usingList1 then list2, list1
                else list1, list2
            listNew.Clear()
            for v in listOld do
                let ok, _ = v.TryGetTarget()
                if ok then listNew.Add(v)
            listOld.Clear()
            usingList1 <- not usingList1

        member this.RawList() =
            this.list :> IReadOnlyList<WeakReference<'t>>

        member this.CollectIfNeeded() =
            let mutable collected = 0
            for w in this.list do
                let ok, _ = w.TryGetTarget()
                if not ok then collected <- collected + 1
            if collected > 0 then this.Collect()

    let getInit (d:Dictionary<'k, 'v>, k:'k, init:unit->'v) : 'v =
        match d.TryGetValue(k) with
        | true, e -> e
        | false, _ ->
            let ret = init()
            d.[k] <- ret
            ret

    let boxObs (obs:IObserver<'t>) : IObserver<obj> =
        { new IObserver<obj> with
            member __.OnCompleted(): unit =  obs.OnCompleted()
            member __.OnError(error: exn): unit = obs.OnError(error)
            member __.OnNext(value: obj): unit = obs.OnNext(unbox value) }

module Agent =
    [<RequireQualifiedAccess>]
    type Message =
        | SetMut of ch:AsyncReplyChannel<unit> * mut:WeakReference<ISignalInternal> * v:obj
        | RegisterMut of ch:AsyncReplyChannel<unit> * mut:WeakReference<ISignalInternal>
        | RegisterDep of 
              ch:AsyncReplyChannel<unit> 
            * source:WeakReference<ISignalInternal> 
            * dependant:WeakReference<ISignalInternal>
        | RegisterDep2 of 
              ch:AsyncReplyChannel<unit> 
            * source1:WeakReference<ISignalInternal> 
            * source2:WeakReference<ISignalInternal> 
            * dependant:WeakReference<ISignalInternal>
        | Stabilize of ch:AsyncReplyChannel<struct(int*Exception)>
        | ToggleSubscribe of
              ch:AsyncReplyChannel<unit>
            * source:WeakReference<ISignalInternal>
            * obs:IObserver<obj>
            * doSubscribe:bool

    type WSignal = WeakReference<ISignalInternal>
    type WSignalList = Utils.WeakList<ISignalInternal>

    type State() =
        member val worldVersion = 0 with get, set
        member val graphVersion = 0 with get, set
        member val muts = Utils.WeakList<ISignalInternal>() with get, set
        member val deps1 = Dictionary<WSignal, WSignalList>() with get
        member val deps2 = Dictionary<WSignal, WSignalList>() with get
        member val observers = Dictionary<WSignal, IObserver<obj> ResizeArray>() with get
        member val pendingUpdates = ResizeArray<WeakReference<ISignalInternal> * obj>() with get, set

    let run (mb:MailboxProcessor<Message>) : Async<unit> =
        let state = State()

        let runUpdates () : unit =
            // Step 1 - Process the mutables
            let mutable source1ValueByDep = Dictionary<ISignalInternal, obj>()
            let mutable source2ValueByDep = Dictionary<ISignalInternal, obj>()
            let allDirty = HashSet<ISignalInternal>()
            let worldVersion = ref 0

            let addAllDirty (signal:ISignalInternal) =
                if not signal.IsDisposed then 
                    allDirty.Add(signal) |> ignore

            let addDirty (dirty:HashSet<ISignalInternal>) (signal:ISignalInternal) =
                if not signal.IsDisposed then 
                    dirty.Add(signal) |> ignore
                    allDirty.Add(signal) |> ignore

            for (wmut, newV) in state.pendingUpdates do                
                let ok, mut = wmut.TryGetTarget()
                if ok then
                    let sameValue = StructuralComparisons.StructuralEqualityComparer.Equals(newV, mut.BoxedVersionedValue.BoxedValue)
                    if not sameValue then
                        let newV_versioned = mut.BoxedVersionedValue.Make newV worldVersion
                        mut.UnsafeSetValue(newV_versioned)              
                        state.worldVersion <- state.worldVersion + 1
                        addAllDirty mut
                    let ok, deps = state.deps1.TryGetValue(mut.WeakRef)
                    if ok then
                        for wd in deps.RawList() do
                            let ok, d = wd.TryGetTarget()
                            if ok then
                                source1ValueByDep.[d] <- newV
                                if not sameValue then addAllDirty d
                    let ok, deps = state.deps2.TryGetValue(mut.WeakRef)
                    if ok then
                        for wd in deps.RawList() do 
                            let ok, d = wd.TryGetTarget()
                            if ok then
                                source2ValueByDep.[d] <- newV
                                if not sameValue then addAllDirty d

            worldVersion := state.worldVersion

            // Step 2 - Process the dependencies of the mutables (recursively)
            // @Todo - Process them in a smarter order to minimize recomputations
            // e.g., Sort by number of deps (recursive)
            let rec processDeps (dirty:HashSet<ISignalInternal>) (n:int) =
                if n > 100 then
                    failwith "Not able to process graph in <= 100 iterations. Check for bad graph setup."
                if dirty.Count > 0 then
                    let newDirty = HashSet<ISignalInternal>()
                
                    for dirtySig in dirty do
                        let ok1, v1 = source1ValueByDep.TryGetValue(dirtySig)
                        let ok2, v2 = source2ValueByDep.TryGetValue(dirtySig)
                        if ok1 || ok2 then
                            let newV = dirtySig.Compute(v1, v2)
                            let sameValue = StructuralComparisons.StructuralEqualityComparer.Equals(newV, dirtySig.BoxedVersionedValue.BoxedValue)
                            if not sameValue then
                                let newV_versioned = dirtySig.BoxedVersionedValue.Make newV worldVersion
                                dirtySig.UnsafeSetValue(newV_versioned)

                            let ok, deps = state.deps1.TryGetValue(dirtySig.WeakRef)
                            if ok then
                                for wd in deps.RawList() do
                                    let ok, d = wd.TryGetTarget()
                                    if ok then
                                        source1ValueByDep.[d] <- newV
                                        if not sameValue then addDirty newDirty d
                            let ok, deps = state.deps2.TryGetValue(dirtySig.WeakRef)
                            if ok then
                                for wd in deps.RawList() do
                                    let ok, d = wd.TryGetTarget()
                                    if ok then
                                        source2ValueByDep.[d] <- newV
                                        if not sameValue then addDirty newDirty d

                    processDeps newDirty (n + 1)

            processDeps (HashSet(allDirty)) 1

            // Notify all observers
            for d in allDirty do
                let v = d.BoxedVersionedValue.BoxedValue
                match state.observers.TryGetValue(d.WeakRef) with
                | true, observersForSource ->
                    for obs in observersForSource do
                        try obs.OnNext(v) 
                        with ex -> obs.OnError(ex)
                | false, _ -> ()

            // Drop collected weak dependencies
            let clearList = HashSet<_>()
            for deps in [state.deps1; state.deps2] do
                for KeyValue(k, deps) in deps do
                    match k.TryGetTarget() with
                    | true, _ -> deps.CollectIfNeeded()
                    | false, _ -> clearList.Add(k) |> ignore
                for k in clearList do deps.Remove(k) |> ignore

                clearList.Clear()
            
            // Drop collected weak subscriptions
            for KeyValue(ws, _) in state.observers do
                let ok, _ = ws.TryGetTarget()
                if not ok then clearList.Add(ws) |> ignore
            
            for ws in clearList do 
                state.observers.Remove(ws) |> ignore
            clearList.Clear()

            state.pendingUpdates.Clear()

        let processMsg (msg:Message) =
            match msg with
            | Message.RegisterMut(ch, mut) ->
                state.muts.Add(mut)
                state.graphVersion <- state.graphVersion + 1
                ch.Reply()
            | Message.RegisterDep(ch, source, dep) ->
                Utils.getInit(state.deps1, source, WSignalList).Add(dep)
                state.graphVersion <- state.graphVersion + 1
                ch.Reply()
            | Message.RegisterDep2(ch, source1, source2, dep) ->
                Utils.getInit(state.deps1, source1, WSignalList).Add(dep)
                Utils.getInit(state.deps2, source2, WSignalList).Add(dep)
                state.graphVersion <- state.graphVersion + 2
                ch.Reply()
            | Message.SetMut(ch, mut, v) ->
                state.pendingUpdates.Add((mut, v))
                ch.Reply()
            | Message.Stabilize(ch) ->
                try
                    runUpdates()
                    ch.Reply(struct(state.worldVersion, null))                
                with
                    ex -> ch.Reply(struct(state.worldVersion, ex))
            | Message.ToggleSubscribe(ch, source, f, doSubscribe) ->
                state.graphVersion <- state.graphVersion + 1
                if doSubscribe then
                    Utils.getInit(state.observers, source, ResizeArray<_>).Add(f)
                    ch.Reply()
                else
                    match state.observers.TryGetValue(source) with
                    | true, subsForSource -> 
                        subsForSource.Remove(f) |> ignore
                        ch.Reply()
                    | false, _ ->
                        // User called .Dipose more than once...
                        // Should throw ObjectDisposed, but probably not worth the
                        // extra state/synchronization needed
                        ()
                
        let rec loop() : Async<unit> =
            async { 
                let! msg = mb.Receive()
                processMsg msg
                return! loop()
            }
        loop()

    let start () : MailboxProcessor<Message> =
        MailboxProcessor<Message>.Start(run)

type IncrementalContext(agent:MailboxProcessor<Agent.Message>, opts: IncrementalOptions) =
    member __.agent = agent
    member __.opts = opts
    
type private Var<'v when 'v: equality>(ctx:IncrementalContext, v:'v) as self =
    let mutable _v = VersionedValue<'v>(v, Unchecked.defaultof<_>)
    let mutable _weakThis = WeakReference<ISignalInternal>(self :> ISignalInternal)
    let mutable _disposed = false

    member __.WeakRef = _weakThis
    member __.SetValue(v:obj) : unit =
        ctx.agent.PostAndReply((fun reply -> Agent.Message.SetMut(reply, _weakThis, v)))

    member __.ToggleSubscribe(obs:IObserver<obj>, doSubscribe:bool) =
        ctx.agent.PostAndReply(fun ch -> Agent.Message.ToggleSubscribe(ch, _weakThis, obs, doSubscribe))

    interface IMutable<'v> with
        member __.Value with get() = _v.Value
        member this.SetValue (v:'v) = this.SetValue(box v)
                
    interface ISignal<'v> with
        member __.Dispose() = _disposed <- true
        member __.IsDisposed = _disposed
        member __.BoxedVersionedValue: IVersionedValue = _v :> _
        member __.VersionedValue: IVersionedValue<'v> = _v :> _
        member __.WeakRef: WeakReference<ISignalInternal> = _weakThis
        member __.UnsafeSetValue v = _v <- unbox v
        member __.Compute (_,_) = box _v
        member __.Value = _v.Value

        member this.Subscribe (obs:IObserver<'v>) : IDisposable =
            let obs' = Utils.boxObs(obs)
                    
            this.ToggleSubscribe(obs', true)
            { new IDisposable with
                member __.Dispose() =
                    this.ToggleSubscribe(obs', false) }
                    
        
type private Signal1<'v, 'o when 'v : equality and 'o: equality>
    (ctx:IncrementalContext, trackable: ISignal<'v>, f: 'v->'o) as self =

    let mutable _v : VersionedValue<'o> = VersionedValue(Unchecked.defaultof<'o>, ref -1)
    let _weakThis = WeakReference<ISignalInternal>(self :> ISignalInternal)
    let mutable _disposed = false

    let getv() =
        if _disposed then
            raise (ObjectDisposedException("Signal1"))
        _v

    member __.ToggleSubscribe(obs:IObserver<obj>, doSubscribe:bool) =
        ctx.agent.PostAndReply(fun ch -> Agent.Message.ToggleSubscribe(ch, _weakThis, obs, doSubscribe))

    interface ISignalInternal with
        member __.Dispose() = _disposed <- true
        member __.IsDisposed = _disposed
        member __.BoxedVersionedValue: IVersionedValue = 
            getv() :> IVersionedValue
        member __.WeakRef: WeakReference<ISignalInternal> = 
            _weakThis
        member __.UnsafeSetValue v =
            _v <- unbox v
        member __.Compute (arg1,_) =
            if _disposed then
                raise (ObjectDisposedException("Signal1"))
            box (f (unbox arg1))

    interface ISignal<'o> with
        member __.VersionedValue: IVersionedValue<'o> = getv() :> _
        member __.Value: 'o = 
            let v = getv()
            let isInitialized = !v.Version <> -1
            if not isInitialized then
                v.UnsafeSetValue((f trackable.Value))
                v.Version := 0
            v.Value

        member this.Subscribe (obs:IObserver<'o>) =
            let obs' = Utils.boxObs(obs)
                    
            this.ToggleSubscribe(obs', true)
            { new IDisposable with
                member __.Dispose() =
                    this.ToggleSubscribe(obs', false) }

                
type private Signal2<'v1, 'v2, 'o when 'v1:equality and 'v2:equality and 'o: equality>
    (ctx:IncrementalContext, s1: ISignal<'v1>, s2: ISignal<'v2>, f: 'v1->'v2->'o) as self =

    let mutable _v : VersionedValue<'o> = VersionedValue(Unchecked.defaultof<'o>, ref -1)
    let _weakThis = WeakReference<ISignalInternal>(self :> ISignalInternal)
    let mutable _disposed = false

    let ensureNotDisposed() =
        if _disposed then
            raise (ObjectDisposedException("Signal2"))

    member __.ToggleSubscribe(obs:IObserver<obj>, doSubscribe:bool) =
        ctx.agent.PostAndReply(fun ch -> Agent.Message.ToggleSubscribe(ch, _weakThis, obs, doSubscribe))

    interface ISignalInternal with
        member __.Dispose() = _disposed <- true
        member __.IsDisposed = _disposed
        member __.BoxedVersionedValue: IVersionedValue =
            ensureNotDisposed()
            _v :> IVersionedValue
        member __.WeakRef: WeakReference<ISignalInternal> = 
            _weakThis
        member __.UnsafeSetValue v =
            Interlocked.Exchange(&_v, unbox v) |> ignore
        member __.Compute (arg1, arg2) =
            ensureNotDisposed()
            box (f (unbox arg1) (unbox arg2))

    interface ISignal<'o> with
        member __.VersionedValue: IVersionedValue<'o> = 
            ensureNotDisposed()
            _v :> _

        member __.Value: 'o =
            ensureNotDisposed()
            let isInitialized = !_v.Version <> -1
            if not isInitialized then
                _v.UnsafeSetValue((f s1.Value s2.Value))
                _v.Version := 0
            _v.Value

        member this.Subscribe (obs:IObserver<'o>) =
            let obs' = Utils.boxObs(obs)
                    
            this.ToggleSubscribe(obs', true)
            { new IDisposable with
                member __.Dispose() =
                    this.ToggleSubscribe(obs', false) }
    
type VarFactory (ctx:IncrementalContext) =
    member x.Create<'t when 't: equality>(v:'t) = 
        let var = new Var<_>(ctx, v)
        let weakR = var.WeakRef
        ctx.agent.PostAndReply(fun reply -> Agent.Message.RegisterMut(reply, weakR))
        var :> IMutable<'t>

type Incremental(?opts: IncrementalOptions) =
    let _opts = opts |> Option.defaultWith IncrementalOptions
    let _event = Event<Exception>()
    let _mb = Agent.start()
    let _ctx = IncrementalContext(_mb, _opts)
    
    member val Var = VarFactory(_ctx) with get

    member __.Stabilize() =
        let struct (vsn, ex) = _ctx.agent.PostAndReply((fun reply -> Agent.Message.Stabilize(reply)))
        if not (obj.ReferenceEquals(null, ex)) then
            _event.Trigger(ex)
            raise ex
        vsn
    
    member __.Map<'a, 'o  when 'a : equality and 'o: equality>(f:'a->'o, sigIn:ISignal<'a>) : ISignal<'o> =
        let sigOut = new Signal1<'a, 'o>(_ctx, sigIn, f)
        let w_sigIn = sigIn.WeakRef
        let w_sigOut = (sigOut :> ISignalInternal).WeakRef
        _ctx.agent.PostAndReply(fun reply -> Agent.Message.RegisterDep(reply, w_sigIn, w_sigOut))
        sigOut :> ISignal<'o>
        
    member __.Map<'a, 'b, 'o when 'a : equality and 'b : equality and 'o: equality>
        (f:'a->'b->'o, sig1:ISignal<'a>, sig2:ISignal<'b>) : ISignal<'o> =
        let sigOut = new Signal2<'a, 'b, 'o>(_ctx, sig1, sig2, f)
        let w_sig1 = sig1.WeakRef
        let w_sig2 = sig2.WeakRef
        let w_sigOut = (sigOut :> ISignalInternal).WeakRef
        _ctx.agent.PostAndReply(fun reply -> Agent.Message.RegisterDep2(reply, w_sig1, w_sig2, w_sigOut))

        sigOut :> ISignal<'o>

    member this.Map<'a, 'b, 'c, 'o when 'a : equality and 'b : equality and 'c : equality and 'o: equality>
        (f:'a->'b->'c->'o, 
         s1:ISignal<'a>, 
         s2:ISignal<'b>,
         s3:ISignal<'c>) : ISignal<'o> =
        let combSig_1_2 : ISignal<struct('a*'b)> = 
            this.Map((fun v1 v2 -> struct (v1, v2)), s1, s2)
        
        let helper struct(v1, v2) (v3:'c) : 'o =
            f v1 v2 v3
        this.Map(helper, combSig_1_2, s3)

    member this.Map<'a, 'b, 'c, 'd, 'o when 'a: equality and 'b: equality and 'c: equality and 'd: equality and 'o: equality>
        (f:'a->'b->'c->'d->'o, 
         sa:ISignal<'a>, 
         sb:ISignal<'b>,
         sc:ISignal<'c>,
         sd:ISignal<'d>) : ISignal<'o> =
        let combSig_a_b : ISignal<struct('a*'b)> = 
            this.Map((fun a b -> struct (a, b)), sa, sb)

        let combSig_c_d : ISignal<struct('c*'d)> = 
            this.Map((fun c d -> struct (c, d)), sc, sd)
        
        let helper comb_a_b comb_c_d : 'o =
            let struct (a, b) = comb_a_b
            let struct (c, d) = comb_c_d
            f a b c d
        
        this.Map(helper, combSig_a_b, combSig_c_d)

    member __.OnError = _event.Publish

    interface IDisposable with
        member __.Dispose() = 
            (_mb :> IDisposable).Dispose()

module Incremental =
    let map (incr:Incremental)
            (sig1:ISignal<'a>)
            (mapping:'a->'o) : ISignal<'o> =
        incr.Map(mapping, sig1)

    let map2 (incr:Incremental)
             (sig1:ISignal<'a>)
             (sig2:ISignal<'b>)
             (mapping:'a->'b->'o) : ISignal<'o> =
        incr.Map(mapping, sig1, sig2)
    
    let map3 (incr:Incremental) 
             (sig1:ISignal<'a>)
             (sig2:ISignal<'b>)
             (sig3:ISignal<'c>)
             (mapping:'a->'b->'c->'o) : ISignal<'o> =
        incr.Map(mapping, sig1, sig2, sig3)

    let map4 (incr:Incremental)
             (sig1:ISignal<'a>)
             (sig2:ISignal<'b>)
             (sig3:ISignal<'c>)
             (sig4:ISignal<'d>)
             (mapping:'a->'b->'c->'d->'o) : ISignal<'o> =
        incr.Map(mapping, sig1, sig2, sig3, sig4)
    
    // @Todo - Add 5 - 10 (or whatever the normal number is)