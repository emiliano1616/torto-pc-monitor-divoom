export PATH := /usr/local/lib/ruby/gems/3.4.0/bin:$(PATH)

.PHONY: run

run:
	dotnet run --project ./src/TortoPcMonitor/TortoPcMonitor.csproj
