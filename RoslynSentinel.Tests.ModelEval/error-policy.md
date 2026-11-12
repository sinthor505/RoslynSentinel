Error-handling policy for the error-policy A/B.
One section per key; the heading text after "### " is the key wire name.
The body line under each heading is the guidance for that case.

### FileNotFound.Edit
The file does not exist. If the task named this file, stop: do not edit or create a different file. Begin your final message with BLOCKED: and quote the error.

### FileNotFound.Lookup
If the task named this file and the error lists no matching file, stop and begin your final message with BLOCKED:. If you chose the path yourself, make one call from the error's options; if that finds nothing, stop with BLOCKED:.

### SymbolNotFound.Edit
The symbol does not exist. If the task named it, stop: do not edit a different symbol or create it. Begin your final message with BLOCKED: and quote the error.

### SymbolNotFound.Lookup
If the task named this symbol and the error lists no similar name, stop and begin your final message with BLOCKED:. If you chose the name yourself, make one call from the error's options; if that finds nothing, stop with BLOCKED:.
