Error-handling policy for the error-policy A/B.
One section per key; the heading text after "### " is the key wire name.
The body line under each heading is the guidance for that case.

### FileNotFound.Edit
If you stop because of this error, start your final message with BLOCKED: and quote the error. The file does not exist. If the task named this file, stop: do not edit or create a different file.

### FileNotFound.Lookup
If you stop because of this error, start your final message with BLOCKED:. If the task named this file and the error lists no matching file, stop. If you chose the path yourself, make one call from the error's options; if that finds nothing, stop.

### SymbolNotFound.Edit
If you stop because of this error, start your final message with BLOCKED: and quote the error. The symbol does not exist. If the task named it, stop: do not edit a different symbol or create it.

### SymbolNotFound.Lookup
If you stop because of this error, start your final message with BLOCKED:. If the task named this symbol and the error lists no similar name, stop. If you chose the name yourself, make one call from the error's options; if that finds nothing, stop.
