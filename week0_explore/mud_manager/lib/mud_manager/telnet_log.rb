require "json"
require "time"

module MudManager
  class TelnetLog
    def initialize(path)
      @file = File.open(path, "a")
      @file.sync = true
      @mutex = Mutex.new
    end

    def record(direction:, text:)
      @mutex.synchronize do
        @file.puts(JSON.generate(at: Time.now.iso8601(3), direction: direction, text: text))
      end
    end

    def close
      @file.close
    end
  end
end
