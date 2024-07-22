defmodule Octocon.Journals do
  @moduledoc false

  import Ecto.Query, warn: false

  alias Octocon.{
    Accounts,
    Alters,
    Repo
  }

  alias Octocon.Journals.{
    AlterJournalEntry,
    GlobalJournalAlters,
    GlobalJournalEntry
  }

  alias OctoconWeb.AlterJournalJSON, as: AlterRenderer
  alias OctoconWeb.GlobalJournalJSON, as: GlobalRenderer

  @all_global_fields GlobalJournalEntry.__struct__()
                     |> Map.drop([:__meta__, :__struct__, :user, :alters])
                     |> Map.keys()

  @bare_global_fields @all_global_fields -- [:content]

  def unwrap_system_identity_where(system_identity, extra \\ []) do
    case system_identity do
      {:system, system_id} ->
        [user_id: system_id] |> Keyword.merge(extra)

      {:discord, _} = identity ->
        [user_id: Accounts.id_from_system_identity(identity, :system)]
        |> Keyword.merge(extra)
    end
  end

  def get_global_journal_entry(system_identity, entry_id) do
    where = unwrap_system_identity_where(system_identity, id: entry_id)

    query =
      GlobalJournalEntry
      |> where(^where)
      |> join(:left, [j], gja in GlobalJournalAlters,
        on: j.id == gja.global_journal_id and gja.user_id == j.user_id
      )
      |> group_by([j], j.id)
      |> select([j, gja], %{entry: j})
      |> select_merge([j, gja], %{alters: fragment("array_agg(?)", gja.alter_id)})

    Repo.one(query)
    |> case do
      # Populate virtual `alters` field
      nil -> nil
      %{entry: entry, alters: [nil]} -> %{entry | alters: []}
      %{entry: entry, alters: alters} -> %{entry | alters: alters}
    end
  end

  def get_global_journal_entries(system_identity, opts \\ []) do
    where = unwrap_system_identity_where(system_identity)

    order_by = Keyword.get(opts, :order_by, desc: :inserted_at)

    fields =
      Keyword.get(opts, :fields, :all)
      |> case do
        :bare ->
          @bare_global_fields

        :all ->
          @all_global_fields

        other when is_list(other) and other != [] ->
          other

        _ ->
          raise ArgumentError,
                "Invalid fields: expected :bare, :all or a non-empty list of field atoms"
      end

    query =
      GlobalJournalEntry
      |> where(^where)
      |> join(:left, [j], gja in GlobalJournalAlters,
        on: j.id == gja.global_journal_id and gja.user_id == j.user_id
      )
      |> group_by([j], j.id)
      |> order_by(^order_by)
      |> select([j], %{entry: struct(j, ^fields)})
      |> select_merge([j, gja], %{alters: fragment("array_agg(?)", gja.alter_id)})

    Repo.all(query)
    |> Enum.map(fn
      # Populate virtual `alters` field
      %{entry: entry, alters: [nil]} -> %{entry | alters: []}
      %{entry: entry, alters: alters} -> %{entry | alters: alters}
    end)
  end

  def create_global_journal_entry(system_identity, title) do
    case Accounts.id_from_system_identity(system_identity, :system) do
      nil ->
        {:error, :not_found}

      system_id ->
        id = Ecto.UUID.generate()

        result =
          %GlobalJournalEntry{
            id: id,
            user_id: system_id
          }
          |> GlobalJournalEntry.changeset(%{title: title})
          |> Repo.insert()

        case result do
          {:ok, entry} ->
            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!(
                "system:#{system_id}",
                "global_journal_entry_created",
                %{
                  entry: GlobalRenderer.data(%{entry | alters: []})
                }
              )
            end)

            {:ok, entry}

          _ ->
            {:error, :changeset}
        end
    end
  end

  def attach_alter_to_global_entry(system_identity, entry_id, alter_identity) do
    case Alters.resolve_alter(system_identity, alter_identity) do
      false ->
        {:error, :alter_not_found}

      alter_id ->
        system_id = Accounts.id_from_system_identity(system_identity, :system)

        %GlobalJournalAlters{
          user_id: system_id,
          global_journal_id: entry_id,
          alter_id: alter_id
        }
        |> GlobalJournalAlters.changeset()
        |> Repo.insert()
        |> case do
          {:ok, _} ->
            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!(
                "system:#{system_id}",
                "global_journal_entry_updated",
                %{
                  entry:
                    GlobalRenderer.data(get_global_journal_entry({:system, system_id}, entry_id))
                }
              )
            end)

            :ok

          _ ->
            {:error, :changeset}
        end
    end
  end

  def detach_alter_from_global_entry(system_identity, entry_id, alter_identity) do
    where = unwrap_system_identity_where(system_identity, global_journal_id: entry_id)

    case Alters.resolve_alter(system_identity, alter_identity) do
      false ->
        {:error, :alter_not_found}

      alter_id ->
        query =
          GlobalJournalAlters
          |> where(^where)
          |> where(alter_id: ^alter_id)

        case Repo.delete_all(query) do
          {1, _} ->
            spawn(fn ->
              system_id = Accounts.id_from_system_identity(system_identity, :system)

              OctoconWeb.Endpoint.broadcast!(
                "system:#{system_id}",
                "global_journal_entry_updated",
                %{
                  entry:
                    GlobalRenderer.data(get_global_journal_entry({:system, system_id}, entry_id))
                }
              )
            end)

            :ok

          _ ->
            {:error, :not_found}
        end
    end
  end

  def update_global_journal_entry(system_identity, entry_id, attrs) do
    case get_global_journal_entry(system_identity, entry_id) do
      nil ->
        {:error, :not_found}

      entry ->
        result =
          entry
          |> GlobalJournalEntry.changeset(attrs)
          |> Repo.update()

        case result do
          {:ok, bare_entry} ->
            system_id = bare_entry.user_id

            entry = get_global_journal_entry({:system, system_id}, entry_id)

            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!(
                "system:#{bare_entry.user_id}",
                "global_journal_entry_updated",
                %{
                  entry: GlobalRenderer.data(entry)
                }
              )
            end)

            {:ok, entry}

          _ ->
            {:error, :changeset}
        end
    end
  end

  def delete_global_journal_entry(system_identity, entry_id) do
    where = unwrap_system_identity_where(system_identity, id: entry_id)

    query =
      GlobalJournalEntry
      |> where(^where)

    case Repo.delete_all(query) do
      {1, _} ->
        spawn(fn ->
          system_id = Accounts.id_from_system_identity(system_identity, :system)

          OctoconWeb.Endpoint.broadcast!(
            "system:#{system_id}",
            "global_journal_entry_deleted",
            %{
              entry_id: entry_id
            }
          )
        end)

        :ok

      _ ->
        {:error, :not_found}
    end
  end

  @all_alter_fields AlterJournalEntry.__struct__()
                    |> Map.drop([:__meta__, :__struct__, :user, :alter])
                    |> Map.keys()

  @bare_alter_fields @all_alter_fields -- [:content]

  def get_alter_journal_entries(system_identity, alter_identity, opts \\ []) do
    case Alters.resolve_alter(system_identity, alter_identity) do
      false ->
        {:error, :no_alter}

      alter_id ->
        where = unwrap_system_identity_where(system_identity)

        order_by = Keyword.get(opts, :order_by, desc: :inserted_at)

        fields =
          Keyword.get(opts, :fields, :all)
          |> case do
            :bare ->
              @bare_alter_fields

            :all ->
              @all_alter_fields

            other when is_list(other) and other != [] ->
              other

            _ ->
              raise ArgumentError,
                    "Invalid fields: expected :bare, :all or a non-empty list of field atoms"
          end

        query =
          AlterJournalEntry
          |> where(^where)
          |> where(alter_id: ^alter_id)
          |> order_by(^order_by)
          |> select([j], struct(j, ^fields))

        Repo.all(query)
    end
  end

  def get_alter_journal_entry(system_identity, entry_id, order_by \\ [desc: :inserted_at]) do
    where = unwrap_system_identity_where(system_identity, id: entry_id)

    query =
      AlterJournalEntry
      |> where(^where)
      |> order_by(^order_by)

    Repo.one(query)
  end

  def create_alter_journal_entry(system_identity, alter_identity, title) do
    system_id = Accounts.id_from_system_identity(system_identity, :system)

    case Alters.resolve_alter(system_identity, alter_identity) do
      false ->
        {:error, :no_alter}

      alter_id ->
        id = Ecto.UUID.generate()

        %AlterJournalEntry{
          id: id,
          user_id: system_id,
          alter_id: alter_id
        }
        |> AlterJournalEntry.changeset(%{title: title})
        |> Repo.insert()
        |> case do
          {:ok, entry} ->
            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!(
                "system:#{system_id}",
                "alter_journal_entry_created",
                %{
                  entry: AlterRenderer.data(entry)
                }
              )
            end)

            {:ok, entry}

          _ ->
            {:error, :changeset}
        end
    end
  end

  def delete_alter_journal_entry(system_identity, entry_id) do
    where = unwrap_system_identity_where(system_identity, id: entry_id)

    query =
      AlterJournalEntry
      |> where(^where)

    case Repo.delete_all(query) do
      {1, _} ->
        spawn(fn ->
          system_id = Accounts.id_from_system_identity(system_identity, :system)

          OctoconWeb.Endpoint.broadcast!(
            "system:#{system_id}",
            "alter_journal_entry_deleted",
            %{
              entry_id: entry_id
            }
          )
        end)

        :ok

      _ ->
        {:error, :not_found}
    end
  end

  def update_alter_journal_entry(system_identity, entry_id, attrs) do
    case get_alter_journal_entry(system_identity, entry_id) do
      nil ->
        {:error, :not_found}

      entry ->
        result =
          entry
          |> AlterJournalEntry.changeset(attrs)
          |> Repo.update()

        case result do
          {:ok, entry} ->
            system_id = entry.user_id

            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!(
                "system:#{system_id}",
                "alter_journal_entry_updated",
                %{
                  entry: AlterRenderer.data(entry)
                }
              )
            end)

            {:ok, entry}

          _ ->
            {:error, :changeset}
        end
    end
  end
end
