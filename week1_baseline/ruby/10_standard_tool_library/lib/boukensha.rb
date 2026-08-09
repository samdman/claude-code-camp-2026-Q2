require_relative "boukensha/version"
require_relative "boukensha/config"
require_relative "boukensha/tasks/player"

module Boukensha
  @quiet  = false
  @debug  = false
  @config = nil

  def self.config
    @config ||= Config.new
  end

  def self.quiet!
    @quiet = true
  end

  def self.loud!
    @quiet = false
  end

  def self.quiet?
    @quiet
  end

  def self.debug!
    @debug = true
  end

  def self.debug?
    @debug
  end

  # One-shot run: send a single task, get a response, return.
  #
  # working_dir:  Context metadata only (returned by Context#working_dir).
  #               Boukensha registers no filesystem tools of its own — plug
  #               in a filesystem MCP server via mcp_servers: if an agent
  #               needs file access.
  #
  # mcp_servers:  Hash of server name => { command:, args:, env:, prefix:,
  #               required: }. Each entry is spawned via Boukensha::Mcp::Client
  #               and its tools registered into the registry (Boukensha::Tools::Mcp).
  #               required: false (default true) downgrades a failed spawn to
  #               a warning instead of raising. nil (default) uses
  #               config.mcp_servers (the mcp_servers: block in settings.yaml).
  #               Pass {} to run with no tools at all.
  def self.run(
    task:,
    system:            nil,
    model:             nil,
    backend:           nil,
    api_key:           nil,
    ollama_host:       "http://localhost:11434",
    log:               nil,
    max_output_tokens: nil,
    working_dir:       Dir.pwd,
    mcp_servers:       nil,
    &block
  )
    cfg           = config                           # loads .env; populates ENV
    task_class    = Tasks::Player
    task_settings = cfg.tasks(task_class.task_name)
    system      ||= task_class.system_prompt(task_settings, user_prompts_dir: cfg.user_prompts_dir, default_prompts_dir: Config::PROMPTS_DIR)
    model       ||= task_class.model(task_settings)
    backend     ||= task_class.provider(task_settings).to_sym
    api_key ||= case backend
                when :anthropic    then ENV["ANTHROPIC_API_KEY"]
                when :openai       then ENV["OPENAI_API_KEY"]
                when :gemini       then ENV["GEMINI_API_KEY"]
                when :ollama_cloud then ENV["OLLAMA_API_KEY"]
                end

    ctx      = Context.new(task: task_class, system: system, working_dir: working_dir)
    registry = Registry.new(ctx)
    clients  = start_mcp_servers(registry, mcp_servers || cfg.mcp_servers)

    RunDSL.new(registry).instance_eval(&block) if block

    be = case backend
         when :anthropic    then Backends::Anthropic.new(api_key: api_key, model: model)
         when :openai       then Backends::OpenAI.new(api_key: api_key, model: model)
         when :gemini       then Backends::Gemini.new(api_key: api_key, model: model)
         when :ollama       then Backends::Ollama.new(host: ollama_host, model: model)
         when :ollama_cloud then Backends::OllamaCloud.new(api_key: api_key, model: model)
         else raise ArgumentError, "Unknown backend #{backend.inspect}. Use :anthropic, :openai, :gemini, :ollama, or :ollama_cloud."
         end

    builder = PromptBuilder.new(ctx, be)
    client  = Client.new(builder)
    effective_max_iterations = task_class.max_iterations(task_settings)
    effective_max_output_tokens = max_output_tokens || task_class.max_output_tokens(task_settings)
    logger  = Logger.new(log: log, snapshot: {
      task:              task_class.task_name,
      max_iterations:    effective_max_iterations,
      max_output_tokens: effective_max_output_tokens,
      model:             model,
      provider:          backend
    })
    agent   = Agent.new(context: ctx, registry: registry, builder: builder, client: client, logger: logger,
                        task_settings: task_settings, max_iterations: effective_max_iterations, max_output_tokens: effective_max_output_tokens)

    ctx.add_message(:user, task)
    agent.run
  ensure
    clients&.each(&:stop)
    logger&.close
  end

  # Interactive REPL — see Boukensha.run for full option documentation.
  def self.repl(
    system:            nil,
    model:             nil,
    backend:           nil,
    api_key:           nil,
    ollama_host:       "http://localhost:11434",
    log:               nil,
    max_output_tokens: nil,
    working_dir:       Dir.pwd,
    mcp_servers:       nil,
    &block
  )
    cfg           = config                           # loads .env; populates ENV
    task_class    = Tasks::Player
    task_settings = cfg.tasks(task_class.task_name)
    system      ||= task_class.system_prompt(task_settings, user_prompts_dir: cfg.user_prompts_dir, default_prompts_dir: Config::PROMPTS_DIR)
    model       ||= task_class.model(task_settings)
    backend     ||= task_class.provider(task_settings).to_sym
    api_key ||= case backend
                when :anthropic    then ENV["ANTHROPIC_API_KEY"]
                when :openai       then ENV["OPENAI_API_KEY"]
                when :gemini       then ENV["GEMINI_API_KEY"]
                when :ollama_cloud then ENV["OLLAMA_API_KEY"]
                end

    ctx      = Context.new(task: task_class, system: system, working_dir: working_dir)
    registry = Registry.new(ctx)
    clients  = start_mcp_servers(registry, mcp_servers || cfg.mcp_servers)

    RunDSL.new(registry).instance_eval(&block) if block

    be = case backend
         when :anthropic    then Backends::Anthropic.new(api_key: api_key, model: model)
         when :openai       then Backends::OpenAI.new(api_key: api_key, model: model)
         when :gemini       then Backends::Gemini.new(api_key: api_key, model: model)
         when :ollama       then Backends::Ollama.new(host: ollama_host, model: model)
         when :ollama_cloud then Backends::OllamaCloud.new(api_key: api_key, model: model)
         else raise ArgumentError, "Unknown backend #{backend.inspect}. Use :anthropic, :openai, :gemini, :ollama, or :ollama_cloud."
         end

    builder = PromptBuilder.new(ctx, be)
    client  = Client.new(builder)
    effective_max_iterations = task_class.max_iterations(task_settings)
    effective_max_output_tokens = max_output_tokens || task_class.max_output_tokens(task_settings)
    logger  = Logger.new(log: log, snapshot: {
      task:              task_class.task_name,
      max_iterations:    effective_max_iterations,
      max_output_tokens: effective_max_output_tokens,
      model:             model,
      provider:          backend
    })

    Repl.new(
      context:    ctx,
      registry:   registry,
      builder:    builder,
      client:     client,
      logger:     logger,
      task_settings: task_settings,
      max_iterations:    effective_max_iterations,
      max_output_tokens: effective_max_output_tokens,
      config_dir: cfg.dir,
      provider:   backend,
      model:      model,
      version:    VERSION,
      api_key:    api_key,
      mcp_server_names: clients.map(&:name)
    ).start
  rescue Interrupt
    puts "\nInterrupted."
  ensure
    clients&.each(&:stop)
    logger&.close
  end

  # Spawn every configured MCP server and register its tools. Returns the
  # Array of started Mcp::Client instances (already registered), so the
  # caller can #stop them in its ensure block. A server with required: false
  # that fails to start is skipped with a warning instead of raising.
  def self.start_mcp_servers(registry, servers)
    return [] unless servers

    servers.filter_map do |server_name, raw_opts|
      opts     = raw_opts.transform_keys(&:to_sym)
      required = opts.key?(:required) ? opts[:required] : true

      client = Mcp::Client.new(
        name:    server_name.to_s,
        command: opts.fetch(:command),
        args:    opts[:args] || [],
        env:     opts[:env] || {}
      )

      begin
        client.start
        Tools::Mcp.register(registry, client: client, prefix: opts[:prefix])
        client
      rescue Mcp::Client::Error => e
        raise unless required == false

        warn "[boukensha] MCP server '#{server_name}' failed to start: #{e.message} (continuing without it)"
        nil
      end
    end
  end
  private_class_method :start_mcp_servers
end

require_relative "boukensha/tool"
require_relative "boukensha/message"
require_relative "boukensha/context"
require_relative "boukensha/errors"
require_relative "boukensha/registry"
require_relative "boukensha/prompt_builder"
require_relative "boukensha/logger"
require_relative "boukensha/backends/base"
require_relative "boukensha/backends/anthropic"
require_relative "boukensha/backends/gemini"
require_relative "boukensha/backends/ollama"
require_relative "boukensha/backends/ollama_cloud"
require_relative "boukensha/backends/openai"
require_relative "boukensha/client"
require_relative "boukensha/agent"
require_relative "boukensha/run_dsl"
require_relative "boukensha/repl"
require_relative "boukensha/mcp/client"
require_relative "boukensha/tools/mcp"
