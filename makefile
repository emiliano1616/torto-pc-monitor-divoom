export PATH := /usr/local/lib/ruby/gems/3.4.0/bin:$(PATH)

.PHONY: run run-debug

run:
	dotnet run --project ./src/TortoPcMonitor/TortoPcMonitor.csproj

run-debug:
	dotnet run --project ./src/TortoPcMonitor/TortoPcMonitor.csproj -D

build:
	dotnet build ./src/TortoPcMonitor/TortoPcMonitor.csproj

clean:
	dotnet clean ./src/TortoPcMonitor/TortoPcMonitor.csproj


