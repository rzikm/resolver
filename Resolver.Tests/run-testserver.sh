#!/bin/bash

docker build -t test-dns-server .
docker run --rm -it -p 1053:53/udp -p 1053:53/tcp test-dns-server
