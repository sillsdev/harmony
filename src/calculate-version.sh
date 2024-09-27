#!/bin/bash

# Pass build number and PR number as command-line params, e.g.:
# calculate-version.sh "${{ github.run_number }}" "${{ github.event.number }}"
# If present, command-line params override env vars

BUILD_NUMBER=$1
PR_NUMBER=$2

if [ -z "BUILD_NUMBER" ]; then
  echo "Required: pass a build number as first parameter"
  echo "Optional: pass a PR number as second parameter"
  exit 2
fi

# If running in CI, current commit is in GITHUB_REF env var
# If not running in CI, use git rev-parse to calculate it
GITHUB_REF=${GITHUB_REF:-$(git rev-parse --symbolic-full-name HEAD)}

# Find most recent tag
DESCRIBE=$(git describe --long --match "v*")
TAG=$(echo "$DESCRIBE" | grep -E -o '^v[0-9]+\.[0-9]+\.[0-9]+')

# Split version number from tag into major/minor/patch sections
MAJOR=$(echo "$TAG" | sed -E 's/^v([0-9]+)\.([0-9]+)\.([0-9]+)$/\1/')
MINOR=$(echo "$TAG" | sed -E 's/^v([0-9]+)\.([0-9]+)\.([0-9]+)$/\2/')
PATCH=$(echo "$TAG" | sed -E 's/^v([0-9]+)\.([0-9]+)\.([0-9]+)$/\3/')

# Calculate prerelease suffix, if any
  SUFFIX=""
if [ -n "$PR_NUMBER" ]; then
  SUFFIX="-PR${PR_NUMBER}.${BUILD_NUMBER}"
elif [ "$GITHUB_REF" = "refs/heads/develop" ]; then
  SUFFIX="-beta.${BUILD_NUMBER}"
elif [ "$GITHUB_REF" = "refs/heads/main" ]; then
  SUFFIX="-rc.${BUILD_NUMBER}"
fi

# Calculate version number bump
# Same logic as GitVersion:
# * "+semver: breaking" or "+semver: major" in commit log will produce major version bump (and reset minor and patch to 0)
# * "+semver: feature" or "+semver: minor" in commit log will produce minor version bump (and reset patch to 0)
# Default is to bump the patch version
# Git log format "%B" is the raw body with no author's email or anything else
COMMIT_COUNT=$(echo "$DESCRIBE" | sed -E 's/^[^-]+-([^-]+)-.*$/\1/')
if [ -n "$COMMIT_COUNT" -a "$COMMIT_COUNT" -gt 0 ]; then
  # Calculate bump based on commit messages
  RAW_LOG=$(git log --format="%B" "$TAG"..HEAD)
  if grep -E '\+semver: (breaking|major)' <<< "$RAW_LOG"; then
    MAJOR=$(($MAJOR + 1))
    MINOR=0
    PATCH=0
  elif grep -E '\+semver: (feature|minor)' <<< "$RAW_LOG"; then
    MINOR=$(($MINOR + 1))
    PATCH=0
  else
    PATCH=$(($PATCH + 1))
  fi
fi

# Set version number variables for MSBuild
export PACKAGE_VERSION=${MAJOR}.${MINOR}.${PATCH}${SUFFIX}
if [ $MAJOR -eq 0 ]; then
  export ASSEMBLY_VERSION=0.${MINOR}.0.0
else
  export ASSEMBLY_VERSION=${MAJOR}.0.0.0
fi
export FILE_VERSION=${MAJOR}.${MINOR}.${PATCH}.${BUILD_NUMBER}

# Put output variables into GITHUB_ENV if it exists
if [ -n "$GITHUB_ENV" ]; then
  echo "PACKAGE_VERSION=${PACKAGE_VERSION}" >> "$GITHUB_ENV"
  echo "ASSEMBLY_VERSION=${ASSEMBLY_VERSION}" >> "$GITHUB_ENV"
  echo "FILE_VERSION=${FILE_VERSION}" >> "$GITHUB_ENV"
else
  echo "PACKAGE_VERSION=${PACKAGE_VERSION}"
  echo "ASSEMBLY_VERSION=${ASSEMBLY_VERSION}"
  echo "FILE_VERSION=${FILE_VERSION}"
fi
