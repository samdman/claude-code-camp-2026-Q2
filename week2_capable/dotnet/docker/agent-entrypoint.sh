#!/bin/sh
set -e

mkdir -p "$BOUKENSHA_DIR"

if [ ! -f "$BOUKENSHA_DIR/settings.yaml" ]; then
    cp /app/settings.template.yaml "$BOUKENSHA_DIR/settings.yaml"
fi

exec dotnet /app/Boukensha.Console.dll "$@"
