#!/bin/bash

AIO_MAX_NR=$(cat /proc/sys/fs/aio-max-nr)
REQUIRED_AIO_MAX_NR=1048576

# Ensure /proc/sys/fs/aio-max-nr is set to a sufficiently high value
if [ "$AIO_MAX_NR" -lt "$REQUIRED_AIO_MAX_NR" ]; then
  echo "ERROR: aio-max-nr is set to $AIO_MAX_NR, which is less than the required $REQUIRED_AIO_MAX_NR. ScyllaDB will not function properly unless this is increased."
  echo "On most Linux systems, you can increase this value by editing /etc/sysctl.conf and adding the line:"
  echo "fs.aio-max-nr = $REQUIRED_AIO_MAX_NR"
  echo "Then run 'sudo sysctl -p' to apply the changes."
  exit 1
fi

# Ensure Docker is installed
if ! command -v docker &> /dev/null; then
  echo "ERROR: Docker could not be found. Please install Docker to proceed."
  exit 1
fi

# Start the Docker containers using the development compose file
echo "Starting Docker containers..."
docker compose -f docker-compose.dev.yml up -d

# If config/dev.exs does not exist, copy it from the example file
if [ ! -f config/dev.exs ]; then
    echo "Creating config/dev.exs from template..."
    cp config/dev-template.exs config/dev.exs
fi

echo "Giving containers a few seconds to initialize..."
sleep 10

# Run the setup script inside the octocon-app container
echo "Running database setup script..."
source ./dev/bin/setup

set +x

echo "Setup complete! You can now run an Octocon development environment with an interactive shell with './dev/bin/iex'."
echo "NOTE: You will need to add a Discord token in config/dev.exs before the app will start properly."