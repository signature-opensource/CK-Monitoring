The log sink for CK.ActivityMonitor: the `GrandOutput` collects the entries emitted by every ActivityMonitor and
dispatches them to configurable handlers.

The handlers - Console, TextFile and BinaryFile (the lossless `.ckmon` format) - can be reconfigured while the
application runs. `LogReader` and `MultiLogReader` read `.ckmon` files back.
