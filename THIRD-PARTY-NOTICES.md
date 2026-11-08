# Third-party components

Spacewise is MIT-licensed (see `LICENSE`). It uses the components below under their own licenses. Check each package's license text before redistributing binaries.

| Component | Use | License |
|---|---|---|
| CommunityToolkit.Mvvm | MVVM helpers | MIT |
| Microsoft.Extensions.Hosting, Microsoft.Data.Sqlite.Core, System.Security.Cryptography.ProtectedData | host, database, key protection | MIT |
| SQLite3MC (SQLite3 Multiple Ciphers) | encrypted local database | MIT |
| Serilog, Serilog.Sinks.File | local logs | Apache-2.0 |
| LLamaSharp (+ Cpu and Vulkan backends, bundled llama.cpp) | local model runtime | MIT |
| Qwen 3.5 2B (GGUF Q4_K_M) | optional local assistant model, downloaded by the user on request, not shipped in the installer | Apache-2.0 (verify on the model card of the pinned revision) |
| WiX Toolset 5 | installer build only | MS-RL (build tool, not redistributed) |
| xUnit v3 | tests only | Apache-2.0 |
