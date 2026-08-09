from __future__ import annotations

from .errors import ApiError
from .logger import Logger


class Agent:
    # Default iteration ceiling. The *enforced* value comes from the
    # max_iterations constructor arg (sourced from Config/Tasks at the
    # run/repl path), which falls back to this constant. 0 (or None)
    # disables the ceiling.
    MAX_ITERATIONS = 25

    # The wind-down call is deliberately short and cheap.
    WRAP_UP_OUTPUT_TOKENS = 400
    WRAP_UP_DIRECTIVE = (
        "You have reached your action limit for this turn. Do not call any more tools.\n"
        "Briefly summarize what you accomplished, what is still unfinished, and the\n"
        "single next action you would take."
    )

    def __init__(
        self,
        *,
        context,
        registry,
        builder,
        client,
        logger: Logger | None = None,
        task_settings: dict | None = None,
        max_iterations: int | None = None,
        max_turn_tokens: int | None = None,
        max_output_tokens: int | None = None,
    ) -> None:
        self.context = context
        self.registry = registry
        self.builder = builder
        self.client = client
        # Ruby's `logger: Logger.new` default is re-evaluated per call, giving
        # every Agent its own session file. Python evaluates a default
        # argument once at def-time, so a literal `Logger()` default here
        # would share one Logger (and one session file) across every Agent
        # ever constructed without an explicit logger=. Construct it lazily
        # instead.
        self.logger = logger if logger is not None else Logger()
        self.max_iterations = self._resolve_max_iterations(task_settings, max_iterations)
        self.max_turn_tokens = int(max_turn_tokens or 0)  # 0 = disabled
        self.max_output_tokens = self._resolve_max_output_tokens(task_settings, max_output_tokens)
        self.iteration = 0

    def run(self) -> str:
        self.context.reset_turn_tokens()
        self._compact_if_needed()

        while True:
            # Two independent ceilings; stop at whichever trips first. Limits
            # are *trigger thresholds*, not hard caps: when one is reached we
            # stop starting new work iterations and make exactly one terminal
            # wind-down call (counted in tokens, but not as another iteration).
            if self._iteration_limit_reached():
                self.logger.limit_reached(kind="max_iterations", n=self.iteration, max=self.max_iterations)
                return self._wrap_up("max_iterations")
            if self._token_limit_reached():
                self.logger.limit_reached(kind="max_tokens", n=self.context.turn_tokens, max=self.max_turn_tokens)
                return self._wrap_up("max_tokens")

            self.iteration += 1
            self.logger.iteration(n=self.iteration, max=self.max_iterations)
            self.logger.prompt(messages=self.context.messages, tools=self.context.tools, context_window=self.context.context_window)

            response = self.client.call(**self._call_opts())
            self.logger.raw(data=response)
            parsed = self.builder.parse_response(response)
            self._record_usage(response)
            self._log_reasoning(parsed["content"])

            if parsed["stop_reason"] == "tool_use":
                self._handle_tool_calls(parsed["content"], response)
            else:
                text = self._extract_text(parsed["content"])
                self._log_response(text=text, response=response, stop_reason=parsed["stop_reason"])
                self.logger.turn_end(reason="completed", iterations=self.iteration, tokens=self.context.turn_tokens)
                self.context.add_message("assistant", text)
                return text

    def _resolve_max_iterations(self, task_settings, explicit) -> int:
        if explicit is not None:
            return int(explicit)
        if task_settings and hasattr(self.context.task, "max_iterations"):
            return self.context.task.max_iterations(task_settings)
        return self.MAX_ITERATIONS

    def _resolve_max_output_tokens(self, task_settings, explicit):
        if explicit is not None:
            return explicit
        if task_settings and hasattr(self.context.task, "max_output_tokens"):
            return self.context.task.max_output_tokens(task_settings)
        return None

    def _iteration_limit_reached(self) -> bool:
        return self.max_iterations > 0 and self.iteration >= self.max_iterations

    def _token_limit_reached(self) -> bool:
        return self.max_turn_tokens > 0 and self.context.turn_tokens >= self.max_turn_tokens

    # Per-call options shared by every model round-trip of the turn.
    def _call_opts(self) -> dict:
        return {"max_output_tokens": self.max_output_tokens} if self.max_output_tokens else {}

    # Add this call's input+output to the cumulative turn total (the spend
    # budget) and refresh the known context size from input_tokens
    # (compaction pressure). Reads response["usage"] verbatim, matching
    # Ruby exactly -- this only actually populates for Anthropic and
    # OpenAI's /v1/responses raw shapes; Gemini/Ollama/OllamaCloud raw
    # payloads don't have a top-level "usage" key, so their token tracking
    # stays inert (a known, accepted Ruby limitation -- not fixed here).
    def _record_usage(self, response: dict) -> None:
        usage = response.get("usage") or {}
        self.context.add_turn_tokens(usage.get("input_tokens"), usage.get("output_tokens"))
        self.context.update_tokens(usage.get("input_tokens"))

    def _compact_if_needed(self) -> None:
        if not self.context.needs_compaction():
            return
        before = self.context.current_tokens
        dropped = self.context.compact_messages()
        self.logger.compaction(before=before, dropped=dropped, context_window=self.context.context_window)

    # One final, tools-disabled model call so the agent ends the turn in
    # character rather than aborting. Runs *outside* the counted loop: it
    # never re-checks the limits (so it cannot re-trigger) and does not
    # increment self.iteration, though its tokens still count toward the
    # reported turn total. Falls back to a deterministic message if the
    # call fails.
    def _wrap_up(self, reason: str) -> str:
        self.context.add_message("user", self.WRAP_UP_DIRECTIVE)
        try:
            response = self.client.call(tools=[], max_output_tokens=self.WRAP_UP_OUTPUT_TOKENS)
            parsed_wrap = self.builder.parse_response(response)
            text = self._extract_text(parsed_wrap["content"])
            text = text if text.strip() else self._fallback_message(reason)
            self._record_usage(response)
            self._log_response(text=text, response=response, stop_reason=parsed_wrap["stop_reason"])
            self.logger.turn_end(reason=reason, iterations=self.iteration, tokens=self.context.turn_tokens)
            self.context.add_message("assistant", text)
            return text
        except ApiError:
            message = self._fallback_message(reason)
            self.logger.turn_end(reason=reason, iterations=self.iteration, tokens=self.context.turn_tokens)
            self.context.add_message("assistant", message)
            return message

    def _fallback_message(self, reason: str) -> str:
        return (
            f"I reached my {self.max_iterations}-action limit for this turn before finishing "
            f"({reason}). Ask me to continue and I'll pick up from here."
        )

    def _extract_text(self, content: list[dict]) -> str:
        return "\n".join(block["text"] for block in content if block["type"] == "text")

    # Emit one `reasoning` event per reasoning block so the viewer can show
    # the model's thinking as a first-class step. Empty, non-redacted
    # blocks are skipped to avoid noise (a redacted/omitted block still
    # renders, since it tells the viewer "the model thought here").
    def _log_reasoning(self, content: list[dict]) -> None:
        for block in content:
            if block.get("type") != "reasoning":
                continue
            redacted = block.get("redacted") is True
            text = str(block.get("text") or "")
            if not text.strip() and not redacted:
                continue
            self.logger.reasoning(text=text, redacted=redacted)

    def _handle_tool_calls(self, content: list[dict], response: dict) -> None:
        tool_calls = [block for block in content if block["type"] == "tool_use"]

        # Log any preamble text that accompanied the tool call (carries no
        # usage -- the placeholder below owns the turn's usage chip), then
        # the placeholder.
        preamble = self._extract_text(content)
        if preamble.strip():
            self.logger.plan(text=preamble)
        plural = "s" if len(tool_calls) != 1 else ""
        self._log_response(text=f"(tool use — {len(tool_calls)} call{plural})", response=response, stop_reason="tool_use")

        self.context.add_message("assistant", content)

        for block in tool_calls:
            name = block["name"]
            args = block["input"]
            use_id = block["id"]

            self.logger.tool_call(name=name, args=args)
            try:
                result = self.registry.dispatch(name, args)
                self.logger.tool_result(name=name, result=result, ok=True)
            except Exception as e:
                result = f"ERROR: {type(e).__name__}: {e}"
                self.logger.tool_result(name=name, result=result, ok=False, error=str(e))

            self.context.add_message("tool_result", str(result), tool_use_id=use_id)

    # stop_reason is passed explicitly (the *normalized* value from
    # parse_response), not read off the raw response -- Ruby 12 fixed this
    # same gap (the raw response has no top-level "stop_reason" key for
    # Gemini/Ollama/OpenAI's /v1/responses, so this was always logging
    # None for every non-Anthropic backend before). The pre-existing
    # task=/backend= cost-metadata logging is kept unchanged.
    def _log_response(self, *, text: str, response: dict, stop_reason) -> None:
        self.logger.response(
            text=text,
            usage=self._normalized_usage(response),
            stop_reason=stop_reason,
            task=self.context.task,
            backend=self.builder.backend,
        )

    def _normalized_usage(self, response: dict):
        if response.get("usage"):
            return response["usage"]
        if response.get("usageMetadata"):
            return response["usageMetadata"]

        usage = {}
        for key in ("prompt_eval_count", "eval_count"):
            if key in response:
                usage[key] = response[key]
        return usage or None
