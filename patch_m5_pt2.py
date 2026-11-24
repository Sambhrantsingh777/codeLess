import sys

with open('src/LlmHarnessAndAgentEngine.cs', 'r') as f:
    content = f.read()

sig = "public static string BuildSystemHarnessPrompt(string mode, LlmHarnessProfile harness)"
new_sig = "public static string BuildSystemHarnessPrompt(string mode, LlmHarnessProfile harness, ByokVendor vendor, string workspaceRoot)"
content = content.replace(sig, new_sig)

# Add AgentContextBuilder calls inside BuildSystemHarnessPrompt
target_sys_prompt = """string basePrompt = string.Format(
@"You are codeLess Autonomous Software Engineering Agent, running inside the [{0}] harness.
Core Engineering Rules:
1. Ground every answer in the <repository_map>, <active_editor>, <diagnostics>, and <bm25_semantic_chunks>.
2. Harness Directive: {1}
3. When modifying existing files, emit surgical SEARCH/REPLACE blocks:
### File: relative/path/to/file.ext
<<<<<<< SEARCH
exact existing lines
=======
updated replacement lines
>>>>>>> REPLACE
4. Never insert dummy placeholder comments. Always write complete, production-ready, working code.",
                hName, hDirective);"""

replacement = """string providerRules = vendor != null ? AgentContextBuilder.GetProviderPrompt(vendor.Family ?? vendor.Id) : AgentContextBuilder.GetProviderPrompt("");
            string instructions = AgentContextBuilder.DiscoverInstructions(workspaceRoot ?? "");
            string envBlock = AgentContextBuilder.BuildEnvBlock(workspaceRoot ?? "");
            
            string basePrompt = string.Format(
@"{0}
{1}
{2}

You are codeLess Autonomous Software Engineering Agent, running inside the [{3}] harness.
Core Engineering Rules:
1. Ground every answer in the <repository_map>, <active_editor>, <diagnostics>, and <bm25_semantic_chunks>.
2. Harness Directive: {4}
3. When modifying existing files, emit surgical SEARCH/REPLACE blocks:
### File: relative/path/to/file.ext
<<<<<<< SEARCH
exact existing lines
=======
updated replacement lines
>>>>>>> REPLACE
4. Never insert dummy placeholder comments. Always write complete, production-ready, working code.",
                envBlock, providerRules, instructions, hName, hDirective);"""
content = content.replace(target_sys_prompt, replacement)

call_target = "string sysPrompt = BuildSystemHarnessPrompt(mode, harness);"
call_replacement = "string sysPrompt = BuildSystemHarnessPrompt(mode, harness, activeVendor, workspaceRoot);"
content = content.replace(call_target, call_replacement)

# Now inject pruning & compaction before CallByokVendorMultiTurnAsync
loop_target = "string llmOutput = await CallByokVendorMultiTurnAsync(activeVendor, sysPrompt, messages, harness.RecommendedTemp).ConfigureAwait(false);"
loop_replacement = """
                    AgentContextBuilder.PruneHistory(messages);
                    
                    int currentTokens = EstimateTokens(sysPrompt + "\\n" + string.Join("\\n", messages.ConvertAll(m => m["content"])));
                    int ctxWindow = (activeVendor != null && activeVendor.ContextWindow > 0) ? activeVendor.ContextWindow : 128000;
                    int maxOut = (activeVendor != null && activeVendor.MaxOutputTokens > 0) ? activeVendor.MaxOutputTokens : 4096;
                    
                    if (currentTokens > ctxWindow - maxOut)
                    {
                        var summaryMessages = new List<Dictionary<string, string>>();
                        summaryMessages.Add(new Dictionary<string, string> { { "role", "system" }, { "content", "Provide a detailed prompt for continuing our conversation above. Focus on what we did, what we're doing, which files we're working on, and what we're going to do next." } });
                        summaryMessages.AddRange(messages);
                        string summary = await CallByokVendorMultiTurnAsync(activeVendor, sysPrompt, summaryMessages, 0.15).ConfigureAwait(false);
                        
                        var lastMsg = messages[messages.Count - 1];
                        messages.Clear();
                        messages.Add(new Dictionary<string, string> { { "role", "assistant" }, { "content", summary } });
                        messages.Add(lastMsg);
                        
                        // Fake feed msg "Context compacted"
                    }
                    
                    string llmOutput = await CallByokVendorMultiTurnAsync(activeVendor, sysPrompt, messages, harness.RecommendedTemp).ConfigureAwait(false);"""
content = content.replace(loop_target, loop_replacement)

with open('src/LlmHarnessAndAgentEngine.cs', 'w') as f:
    f.write(content)
